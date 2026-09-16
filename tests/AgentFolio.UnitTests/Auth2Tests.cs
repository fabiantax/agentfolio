using System.Net;
using System.Text;
using AgentFolio.Platform;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Slices.Auth2;

namespace AgentFolio.UnitTests;

/// <summary>
/// s2-02 GitHub OAuth endpoint tests: mocked GitHub over DelegatingHandler +
/// WebApplicationFactory with RemoveAll-TenantRegistry temp-dir isolation.
/// The af_oauth round-trip is exercised via raw Cookie headers (the in-memory
/// WebApplicationFactory client's cookie jar is unreliable across requests).
/// </summary>
public class Auth2Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("auth2-test-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public Auth2Tests()
    {
        Environment.SetEnvironmentVariable("GITHUB_CLIENT_ID", "test-client-id");
        Environment.SetEnvironmentVariable("GITHUB_CLIENT_SECRET", "test-client-secret");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                var reg = new TenantRegistry(Path.Combine(_dir, "global.db"));
                reg.EnsureSchemaAsync().GetAwaiter().GetResult();
                services.RemoveAll<TenantRegistry>();
                services.RemoveAll<TenantShardRegistry>();
                services.AddSingleton(reg);
                services.AddSingleton(new TenantShardRegistry(reg.GlobalDbPath));
                services.AddHttpClient("github")
                    .AddHttpMessageHandler(_ => new FakeGithubHandler());
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("GITHUB_CLIENT_ID", null);
        Environment.SetEnvironmentVariable("GITHUB_CLIENT_SECRET", null);
    }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.BaseAddress = new Uri("http://localhost");
        return client;
    }

    private (string setCookie, string state, string verifier) Start()
    {
        var response = NewClient().GetAsync("/auth/github/start").GetAwaiter().GetResult();
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var v)
            ? string.Join("|", v) : "";
        var (state, verifier) = StartCookieValue(setCookie);
        return (setCookie, state, verifier);
    }

    private static (string state, string verifier) StartCookieValue(string setCookie)
    {
        var start = setCookie.IndexOf("af_oauth=") + "af_oauth=".Length;
        var end = setCookie.IndexOf(';', start);
        var decoded = Uri.UnescapeDataString(setCookie[start..end]);
        var sep = decoded.IndexOf('.');
        return (decoded[..sep], decoded[(sep + 1)..]);
    }

    [Fact]
    public async Task Start_sets_af_oauth_cookie_and_redirects_to_github()
    {
        var response = await NewClient().GetAsync("/auth/github/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize",
            response.Headers.Location!.ToString());
        Assert.Contains("scope=read%3Auser", response.Headers.Location!.ToString());
        Assert.Contains("code_challenge_method=S256", response.Headers.Location!.ToString());

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("|", values) : "";
        Assert.Contains("af_oauth=", cookies);
        Assert.Contains("httpOnly", cookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=300", cookies, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_happy_path_signs_in_and_provisions_tenant()
    {
        var (_, state, verifier) = Start();

        var client = NewClient();
        client.DefaultRequestHeaders.Add("Cookie",
            $"af_oauth={Uri.EscapeDataString($"{state}.{verifier}")}");
        var callback = await client.GetAsync(
            $"/auth/github/callback?code=abc&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.ToString());
        // Signed in via the existing cookie scheme.
        Assert.True(callback.Headers.TryGetValues("Set-Cookie", out var cb)
            && string.Join("|", cb).Contains("agentfolio_auth="));

        // Tenant provisioned in the registry for the mocked github id.
        var registry = _factory.Services.GetRequiredService<TenantRegistry>();
        var user = await registry.GetOrCreateUserTenantAsync(FakeGithubHandler.GithubId);
        Assert.Equal("github-" + FakeGithubHandler.GithubId, user.Slug);
    }

    [Fact]
    public async Task Callback_state_mismatch_returns_400()
    {
        var (_, state, verifier) = Start();

        var client = NewClient();
        client.DefaultRequestHeaders.Add("Cookie",
            $"af_oauth={Uri.EscapeDataString($"{state}.{verifier}")}");
        var response = await client.GetAsync(
            "/auth/github/callback?code=abc&state=wrong-state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Same_github_id_twice_returns_same_tenant_id()
    {
        var registry = _factory.Services.GetRequiredService<TenantRegistry>();
        var first = await registry.GetOrCreateUserTenantAsync(FakeGithubHandler.GithubId);
        var second = await registry.GetOrCreateUserTenantAsync(FakeGithubHandler.GithubId);
        Assert.Equal(first.TenantId, second.TenantId);
    }

    [Fact]
    public async Task Client_id_unset_returns_503()
    {
        Environment.SetEnvironmentVariable("GITHUB_CLIENT_ID", null);
        var response = await NewClient().GetAsync("/auth/github/start");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("github_oauth_not_configured", body);
    }

    [Fact]
    public async Task Login_page_has_github_link()
    {
        var html = await NewClient().GetStringAsync("/login");
        Assert.Contains("/auth/github/start", html);
        Assert.Contains("Sign in with GitHub", html);
    }

    [Fact]
    public void Code_challenge_is_s256_of_verifier()
    {
        var verifier = GitHubOauth.NewVerifier();
        var challenge = GitHubOauth.CodeChallengeS256(verifier);
        Assert.NotEqual(verifier, challenge);
        Assert.Equal(challenge, GitHubOauth.CodeChallengeS256(verifier));
    }
}

/// <summary>Fake GitHub token + user endpoints; accepts any code/verifier.</summary>
internal sealed class FakeGithubHandler : DelegatingHandler
{
    public const long GithubId = 424242;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => RespondAsync(request);

    private Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.ToString().Contains("/login/oauth/access_token"))
        {
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"access_token":"fake-token","token_type":"bearer"}"""));
        }
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.ToString().Contains("api.github.com/user"))
        {
            return Task.FromResult(Json(HttpStatusCode.OK,
                $$"""{"id":{{GithubId}},"login":"testuser"}"""));
        }
        return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
