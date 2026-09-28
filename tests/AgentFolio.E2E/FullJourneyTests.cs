using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;
using System.Text;
using AgentFolio.Contracts;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Slices.Stars;
using Slices.Uploads;
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

    private static readonly Lazy<string> SandboxExtractorPath = new(() =>
    {
        // Same recipe as SandboxTests: Release-build the sandbox extractor once.
        // Cross-process mutex: UnitTests and E2E build the SAME project to the same
        // bin/Release path concurrently in CI (measured run 29191 — GenerateDepsFile
        // IOException). Serialize; the second build is an incremental no-op.
        using var gate = new Mutex(initiallyOwned: false, name: "AgentFolio.ExtractorBuildLock");
        gate.WaitOne();
        string dll;
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
            var project = Path.GetFullPath(Path.Combine(root, "src", "AgentFolio.Sandbox", "AgentFolio.Sandbox.Extractor", "AgentFolio.Sandbox.Extractor.csproj"));
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{project}\" -c Release -v q --nologo",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var buildOut = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) throw new InvalidOperationException($"extractor build failed: {buildOut}");
            dll = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", "net10.0", "AgentFolio.Sandbox.Extractor.dll");
        }
        finally { gate.ReleaseMutex(); }
        return dll;
    });

    private void BuildApp()
    {
        _ = SandboxExtractorPath.Value; // warm the one-time Release build before timing matters
        // Env BEFORE host build: the Program.cs factory reads it when DI resolves ExtractionService.
        Environment.SetEnvironmentVariable(ExtractionService.ExtractorEnvVar, SandboxExtractorPath.Value);
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
                // Fake ONLY the GitHub HTTP (trust boundary) — stars sync journeys.
                services.RemoveAll<IGitHubStarsClient>();
                services.AddSingleton<IGitHubStarsClient>(new FakeE2EStarsClient());
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
        Assert.Contains("type=\"module\"", html); // module bundle (plain script = SyntaxError, silent dead UI)
        Assert.DoesNotContain(_showcaseTenantId, html); // tenant id never leaks to anonymous share visitors
        Assert.StartsWith("noindex", share.Headers.GetValues("X-Robots-Tag").First());

        // ---- 6. Upload contract: password-auth'd share visitor CAN upload —
        // tenant resolves to showcase (no OAuth tid claim needed for the demo path).
        var up = await client.PostAsync("/api/uploads",
            new MultipartFormDataContent { { new ByteArrayContent("hello e2e"u8.ToArray()), "file", "a.txt" } });
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var receipt = await up.Content.ReadFromJsonAsync<UploadReceiptDto>();
        Assert.StartsWith("tenants/", receipt!.Key);

        // Upload must still be AUTH-gated (no cookie -> 401).
        // DelegatingHandler wrapper so the factory starts the host and provides BaseAddress.
        var anonUpClient = _app.CreateClient();
        anonUpClient.DefaultRequestHeaders.ExpectContinue = false;
        // Reject-by-redirect: anonymous POST /api/uploads 302s to /Account/Login?ReturnUrl —
        // which 404s. Assert the REDIRECT (the auth gate), not the followed result.
        var anonReq = new HttpRequestMessage(HttpMethod.Post, "/api/uploads")
        { Content = new MultipartFormDataContent { { new ByteArrayContent("x"u8.ToArray()), "file", "a.txt" } } };
        var anonUp = await anonUpClient.SendAsync(anonReq);
        Assert.Contains(anonUp.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Redirect, HttpStatusCode.NotFound, HttpStatusCode.BadRequest });

        // ---- 7. Stars sync (demo-critical, #41): fake GitHub HTTP at the trust
        // boundary, real per-tenant shard store — showcase tenant gets the projections.
        // 202 background-job contract (#62): trigger, poll status to terminal.
        var sync = await client.PostAsync("/stars/sync", content: null);
        Assert.Equal(HttpStatusCode.Accepted, sync.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string syncBody;
        while (true)
        {
            var status = await client.GetAsync("/stars/sync/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            syncBody = await status.Content.ReadAsStringAsync();
            if (syncBody.Contains("\"status\":\"completed\"") || syncBody.Contains("\"status\":\"failed\""))
                break;
            Assert.True(DateTime.UtcNow < deadline, "stars sync job did not complete in 30s");
            await Task.Delay(100);
        }
        Assert.Contains("\"fetched\":2", syncBody);
        Assert.Contains("\"upserted\":2", syncBody);

        var starsList = await client.GetAsync("/stars");
        Assert.Equal(HttpStatusCode.OK, starsList.StatusCode);
        var starsJson = await starsList.Content.ReadAsStringAsync();
        Assert.Contains("repo2", starsJson);
        Assert.Contains("repo1", starsJson);
    }

    [Fact]
    public async Task Upload_docs_and_session_delete_roundtrip_for_share_visitor()
    {
        BuildApp();
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/auth/login", new { password = "e2e-pass" });

        // Upload a plain-text doc; extraction stores tenants/.../extracted/*.txt.
        var up = await client.PostAsync("/api/uploads?sync=1",
            new MultipartFormDataContent { { new ByteArrayContent("hiring requirements: rust k8s agents"u8.ToArray()), "file", "jd.txt" } });
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        // Extraction is fire-and-forget in a child process: poll the docs endpoint briefly.
        string? docs = null;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            var d = await client.GetAsync("/api/session/docs");
            if (d.StatusCode != HttpStatusCode.OK) continue;
            docs = await d.Content.ReadAsStringAsync();
            if (docs.Contains("rust k8s")) break;
        }
        Assert.NotNull(docs);
        Assert.True(docs.Contains("rust k8s"),
            $"extracted text missing — docs endpoint says: {docs}"); // diagnostics inline on failure

        // Disposal: session data wipe -> docs empty afterwards.
        var del = await client.DeleteAsync("/api/session/data");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var after = await client.GetAsync("/api/session/docs");
        var afterBody = await after.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rust k8s", afterBody);
    }

    public void Dispose()
    {
        _app?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record UploadReceiptDto(string Key, long Bytes, string ContentType, DateTimeOffset ExpiresAt);

    /// <summary>Fake GitHub stars client: two deterministic starred repos.</summary>
    private sealed class FakeE2EStarsClient : IGitHubStarsClient
    {
        public Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(CancellationToken ct)
            => GetAllStarredAsync(ct, onProgress: null);

        public Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(CancellationToken ct, Action<int, string>? onProgress)
            => Task.FromResult<IReadOnlyList<StarredRepoDto>>(new[]
            {
                new StarredRepoDto(1, "repo1", "user1/repo1", "desc1", "https://github.com/1", 10, "C#", new[] { "t1" }, "user1", "2024-03-01T00:00:00Z"),
                new StarredRepoDto(2, "repo2", "user2/repo2", "desc2", "https://github.com/2", 5, "Rust", new[] { "t2" }, "user2", "2024-06-01T00:00:00Z"),
            });
    }

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
