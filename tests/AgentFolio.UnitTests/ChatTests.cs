using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentFolio.Contracts;
using AgentFolio.Platform;
using AgentFolio.Platform.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Slices.Chat;
using Slices.Uploads;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AgentFolio.UnitTests;

[Collection("password-hash-env")]
public class ChatTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"agentfolio-chat-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p)) File.Delete(p);
        Environment.SetEnvironmentVariable("AGENTFOLIO_DB_PATH", null);
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar, null);
    }

    private static HttpClient LoggedInClient(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar, PasswordGate.HashPassword("hunter2"));
        var client = factory.CreateClient();
        var login = client.PostAsJsonAsync("/auth/login", new { password = "hunter2" }).GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
        return client;
    }

    private WebApplicationFactory<Program> FactoryWithFakeNim()
    {
        Environment.SetEnvironmentVariable("AGENTFOLIO_DB_PATH", _dbPath);
        // Ensure chat_messages schema on the temp DB before requests hit the store.
        var bootstrap = new SqliteStore(_dbPath);
        bootstrap.OpenAsync().GetAwaiter().GetResult();
        bootstrap.EnsureSchemaAsync().GetAwaiter().GetResult();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore>(new SqliteConversationStore($"Data Source={_dbPath}"));
                services.AddKeyedSingleton<IChatClient>("nim", new MultiChunkFakeChatClient());
            });
        });
    }

    [Fact]
    public async Task PostChat_WithoutCookie_Returns401()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/chat", new ChatRequest(null, "hi", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostChat_WithAuth_StreamsTokenEventsThenDoneLine()
    {
        await using var factory = FactoryWithFakeNim();
        var client = LoggedInClient(factory);

        var response = await client.PostAsJsonAsync("/chat", new ChatRequest(null, "hello", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var payloads = lines.Select(l => l.StartsWith("data: ") ? l["data: ".Length..] : l).ToList();

        // at least 2 token SSE lines
        var tokens = payloads
            .Select(p => TryDeserialize<ChatTokenEvent>(p))
            .Where(t => t is not null)
            .ToList();
        Assert.True(tokens.Count >= 2, $"expected >=2 token lines, got: {body}");

        // ends with done line: JSON with ConversationId + empty citations
        var done = payloads[^2]; // last is [DONE]
        JsonElement completion;
        try { completion = JsonSerializer.Deserialize<JsonElement>(done); }
        catch (JsonException) { completion = default; }
        Assert.True(completion.ValueKind == JsonValueKind.Object, $"expected completion JSON, got: {body}");
        Assert.True(completion.TryGetProperty("conversationId", out var cid) && cid.GetString() is { Length: > 0 });
        Assert.Equal(0, completion.GetProperty("citations").GetArrayLength());
        Assert.Equal("[DONE]", payloads[^1]);
    }

    [Fact]
    public async Task PostChat_SameConversationId_PersistsBothTurns()
    {
        await using var factory = FactoryWithFakeNim();
        var client = LoggedInClient(factory);
        var conversationId = Guid.NewGuid().ToString();

        await client.PostAsJsonAsync("/chat", new ChatRequest(conversationId, "first question", null));

        // Second call with the SAME ConversationId.
        var second = await client.PostAsJsonAsync("/chat", new ChatRequest(conversationId, "second question", null));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var store = new SqliteConversationStore($"Data Source={_dbPath}");
        var turns = new List<StoredChatMessage>();
        await foreach (var m in store.LoadConversationAsync(conversationId))
            turns.Add(m);

        Assert.Equal(4, turns.Count); // 2 user + 2 assistant
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("first question", turns[0].Text);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("user", turns[2].Role);
        Assert.Equal("second question", turns[2].Text);
        Assert.Equal("assistant", turns[3].Role);
        Assert.All(turns, t => Assert.Equal(conversationId, t.ConversationId));
    }

    [Fact]
    public async Task PostChat_WithSessionDoc_CitationsResolveFromDocMarkers()
    {
        // Real tenancy + object store so the doc-context path (upload -> extracted ->
        // chat -> [DOC n] markers -> citations) runs end-to-end, fake only the model.
        Environment.SetEnvironmentVariable("AGENTFOLIO_DB_PATH", _dbPath);
        var dir = Directory.CreateTempSubdirectory("af-cite-");
        // The sandbox extractor must be built + pinned like SandboxTests/E2E (the
        // default AppContext.BaseDirectory path does not exist in the test host).
        using (var gate = new System.Threading.Mutex(false, "AgentFolio.ExtractorBuildLock"))
        {
            gate.WaitOne();
            try
            {
                var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
                var project = Path.Combine(repoRoot, "src", "AgentFolio.Sandbox", "AgentFolio.Sandbox.Extractor", "AgentFolio.Sandbox.Extractor.csproj");
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
                Environment.SetEnvironmentVariable(ExtractionService.ExtractorEnvVar,
                    Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", "net10.0", "AgentFolio.Sandbox.Extractor.dll"));
            }
            finally { gate.ReleaseMutex(); }
        }
        var registry = new AgentFolio.Platform.Tenancy.TenantRegistry(Path.Combine(dir.FullName, "global.db"));
        await registry.EnsureSchemaAsync();
        await registry.CreateTenantAsync("showcase");
        var shards = new AgentFolio.Platform.Tenancy.TenantShardRegistry(registry.GlobalDbPath);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<AgentFolio.Platform.Tenancy.TenantRegistry>();
                    services.RemoveAll<AgentFolio.Platform.Tenancy.TenantShardRegistry>();
                    services.AddSingleton(registry);
                    services.AddSingleton(shards);
                    services.RemoveAll<IChatClient>();
                    services.AddKeyedSingleton<IChatClient>("nim", new CitingFakeChatClient());
                });
            });
            var client = LoggedInClient(factory);

            // Seed one session doc carrying uploader provenance (Source: URL).
            var doc = "Source: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14\n"
                    + "C# 14 extension members add extension properties and static members.\n";
            var up = await client.PostAsync("/api/uploads?sync=1",
                new MultipartFormDataContent { { new System.Net.Http.ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(doc)), "file", "csharp14.txt" } });
            up.EnsureSuccessStatusCode();
            var upBody = await up.Content.ReadAsStringAsync();
            Assert.Contains("\"extracted\":true", upBody);

            var response = await client.PostAsJsonAsync("/chat", new ChatRequest(null, "which feature?", null));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            // Streamed tokens carry NO raw markers (stripped before SSE).
            Assert.DoesNotContain("[DOC", body[..body.IndexOf("\"citations\"", StringComparison.Ordinal)]);

            // Completion event resolves the cited doc into a real citation.
            var doneLine = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .First(l => l.StartsWith("data: ") && l.Contains("\"citations\""));
            using var doc2 = System.Text.Json.JsonDocument.Parse(doneLine["data: ".Length..]);
            var cits = doc2.RootElement.GetProperty("citations");
            Assert.Equal(1, cits.GetArrayLength());
            var cit = cits[0];
            Assert.Equal("https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14", cit.GetProperty("url").GetString());
            Assert.Equal("Session document 1 — C# 14 extension members add extension properties and stat…",
                cit.GetProperty("title").GetString());
            Assert.Contains("extension members", cit.GetProperty("snippet").GetString());
        }
        finally
        {
            try { Directory.Delete(dir.FullName, recursive: true); } catch { }
        }
    }

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Fake IChatClient streaming multiple chunks (test DI override for keyed 'nim').</summary>
    private sealed class MultiChunkFakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "chunkA chunkB chunkC")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in new[] { "chunkA ", "chunkB ", "chunkC" })
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Fake model that cites [DOC 1] like an instructed model would.</summary>
    private sealed class CitingFakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer [DOC 1]")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "The extension members feature [DOC 1] ships with ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, ".NET 10 [DOC 1].");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
