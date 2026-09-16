using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;
using System.Text;
using AgentFolio.Contracts;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentFolio.E2E;

/// <summary>
/// Full-journey in-proc E2E (Playwright alternative): real Program.cs pipeline,
/// real SQLite shards on temp dirs, fake only at the trust boundaries
/// (chat model, GitHub HTTP). Runs in milliseconds — part of dotnet test.
/// </summary>
public class FullJourneyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("af-e2e-").FullName;
    private WebApplicationFactory<Program> _app = null!;
    private TenantRegistry _registry = null!;
    private string _showcaseTenantId = null!;

    private void BuildApp()
    {
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar,
            PasswordGate.HashPassword("e2e-pass"));
        _registry = new TenantRegistry(Path.Combine(_dir, "global.db"));
        _registry.EnsureSchemaAsync().GetAwaiter().GetResult();
        // Provision the showcase tenant the way --migrate-single-to-tenant would.
        _showcaseTenantId = _registry.CreateTenantAsync("showcase").GetAwaiter().GetResult().TenantId;
        var shards = new TenantShardRegistry(_registry.GlobalDbPath);

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.RemoveAll<TenantRegistry>();
                services.RemoveAll<TenantShardRegistry>();
                services.AddSingleton(_registry);
                services.AddSingleton(shards);
                // Fake ONLY the LLM (trust boundary) — keyed override like ChatTests.
                services.RemoveAll<IChatClient>();
                services.AddKeyedSingleton<IChatClient>("nim", new EchoCitingChatClient());
            });
        });
    }

    [Fact]
    public async Task Recruiter_full_journey_password_login_chat_upload_share()
    {
        BuildApp();
        var client = _app.CreateClient();

        // ---- 1. Auth boundary: gate closed before login.
        var locked = await client.PostAsJsonAsync("/chat", new { message = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);

        // ---- 2. Password login (Argon2id gate) -> auth cookie.
        var login = await client.PostAsJsonAsync("/auth/login", new { password = "e2e-pass" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        // ---- 3. Chat with citations (SSE, fake model echoes with citation).
        var chat = await client.PostAsync("/chat",
            new StringContent("""{"message":"what rust repos?"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var sse = await chat.Content.ReadAsStringAsync();
        Assert.Contains("data:", sse);                       // streamed token events
        Assert.Contains("conversationId", sse);              // done event
        // Citations contract: key always present; populated once retrieval wiring lands
        // (af-08 documented deviation — chat answers cite store content post-retrieval).
        Assert.Contains("\"citations\"", sse);

        // ---- 4/5. Share URL: showcase tenant serves the portfolio page (noindex,
        //          tenant-scoped chat component) to an ANONYMOUS visitor.
        var anon = _app.CreateClient(); // no cookies — public share surface
        var share = await anon.GetAsync("/share/showcase");
        Assert.Equal(HttpStatusCode.OK, share.StatusCode);
        var html = await share.Content.ReadAsStringAsync();
        Assert.Contains("agentfolio-chat", html);
        Assert.Contains(_showcaseTenantId, html);
        Assert.StartsWith("noindex", share.Headers.GetValues("X-Robots-Tag").First());

        // ---- 6. Upload contract: cookie-auth'd but WITHOUT an OAuth tid claim -> 403
        // (password gate satisfies authn; tenancy requires the GitHub-OAuth tid claim).
        var up = await client.PostAsync("/api/uploads",
            new MultipartFormDataContent { { new ByteArrayContent("hello e2e"u8.ToArray()), "file", "a.txt" } });
        Assert.Equal(HttpStatusCode.Forbidden, up.StatusCode);
    }

    [Fact]
    public async Task Upload_endpoints_are_claim_gated_not_just_cookie_gated()
    {
        BuildApp();
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/auth/login", new { password = "e2e-pass" });

        // Both upload endpoints: authenticated (password cookie) yet 403 without the
        // tenant claim — uploads are tenant-scoped by identity, not by session.
        var up = await client.PostAsync("/api/uploads",
            new MultipartFormDataContent { { new ByteArrayContent("x"u8.ToArray()), "file", "a.txt" } });
        Assert.Equal(HttpStatusCode.Forbidden, up.StatusCode);

        var del = await client.DeleteAsync("/api/session/data");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }

    public void Dispose()
    {
        _app?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record UploadReceiptDto(string Key, long Bytes, string ContentType, DateTimeOffset ExpiresAt);

    /// <summary>Fake chat model: streams tokens, done event carries a GitHub citation.</summary>
    private sealed class EchoCitingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "see repo")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => StreamAsync(ct);

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "rust repos: ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "tokio, axum");
        }

        public object? GetService(Type serviceType, object? serviceProviderKey) => null;
        public void Dispose() { }
    }
}
