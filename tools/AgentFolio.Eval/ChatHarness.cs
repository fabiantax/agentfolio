using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentFolio.Agents;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentFolio.Eval;

/// <summary>
/// End-to-end eval harness (issue #44): boots the REAL Program pipeline
/// (WebApplicationFactory), seeds the frozen set's sources through the REAL
/// upload + sandbox-extraction path, then asks every question through the
/// REAL /chat SSE endpoint against a real OpenAI-compatible model. Fakes:
/// none. The only boundary objects are the temp-dir SQLite stores, exactly
/// like the E2E suite.
/// </summary>
public sealed class ChatHarness : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("af-eval-").FullName;
    private WebApplicationFactory<Program>? _app;

    private static readonly Lazy<string> SandboxExtractorPath = new(() =>
    {
        // Same recipe as SandboxTests/E2E (incl. the cross-process build mutex —
        // parallel test processes build the SAME project to one bin/Release path).
        using var gate = new Mutex(initiallyOwned: false, name: "AgentFolio.ExtractorBuildLock");
        gate.WaitOne();
        string dll;
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
            var project = Path.GetFullPath(Path.Combine(root, "src", "AgentFolio.Sandbox", "AgentFolio.Sandbox.Extractor", "AgentFolio.Sandbox.Extractor.csproj"));
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{project}\" -c Release -v q --nologo",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var buildOut = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) throw new InvalidOperationException($"extractor build failed: {buildOut}");
            dll = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", "net10.0", "AgentFolio.Sandbox.Extractor.dll");
        }
        finally { gate.ReleaseMutex(); }
        return dll;
    });

    /// <summary>Boots the app with a real model endpoint (NimChatClientFactory wiring).</summary>
    public HttpClient Boot(string apiBase, string apiKey, string model)
    {
        // ExtractionService spawns `dotnet <extractor.dll>` via DOTNET_ROOT. Under
        // `dotnet run` (unlike `dotnet test`) DOTNET_ROOT is typically unset, and the
        // service then resolves the CURRENT apphost as "dotnet" — spawning this CLI
        // as the extractor. Derive the real runtime root from the loaded runtime dir.
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is null)
        {
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(); // <root>/shared/Microsoft.NETCore.App/10.x/
            var dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(runtimeDir))));
            if (dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, "dotnet")))
                Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
        }
        Environment.SetEnvironmentVariable(Slices.Uploads.ExtractionService.ExtractorEnvVar, SandboxExtractorPath.Value);
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar, PasswordGate.HashPassword("eval-pass"));

        var registry = new TenantRegistry(Path.Combine(_dir, "global.db"));
        registry.EnsureSchemaAsync().GetAwaiter().GetResult();
        registry.CreateTenantAsync("showcase").GetAwaiter().GetResult();
        var shards = new TenantShardRegistry(registry.GlobalDbPath);

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // dotnet run (CLI context) resolves the Mvc.Testing content-root manifest
            // to a wrong path (<repo>/AgentFolio.Api instead of <repo>/src/AgentFolio.Api)
            // — pin it explicitly like the E2E suite's implicit resolution does.
            var apiRoot = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentFolio.Api"));
            b.UseSetting("contentRoot", apiRoot);
            b.ConfigureServices(services =>
            {
                services.RemoveAll<TenantRegistry>();
                services.RemoveAll<TenantShardRegistry>();
                services.AddSingleton(registry);
                services.AddSingleton(shards);
                // REAL model client (trust boundary is the endpoint itself, not a fake).
                services.RemoveAll<IChatClient>();
                services.AddKeyedSingleton<IChatClient>("nim",
                    NimChatClientFactory.Create(apiBase, apiKey, model));
            });
        });
        var client = _app.CreateClient();
        var login = client.PostAsJsonAsync("/auth/login", new { password = "eval-pass" }).GetAwaiter().GetResult();
        if (!login.IsSuccessStatusCode)
            throw new InvalidOperationException($"eval login failed: {(int)login.StatusCode}");
        return client;
    }

    /// <summary>Uploads one source as .txt through the real upload+extraction path (sync mode).</summary>
    public async Task SeedSourceAsync(HttpClient client, EvalSource source)
    {
        // Prepend the product's uploader-provenance header (chat reads `Source: <url>`
        // to resolve citation URLs) — same contract a real user's sourced doc carries.
        var content = $"Source: {source.Url}\n{source.Text}";
        var up = await client.PostAsync("/api/uploads?sync=1",
            new MultipartFormDataContent
            {
                { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", $"{source.Id}.txt" }
            });
        up.EnsureSuccessStatusCode();
        var body = await up.Content.ReadAsStringAsync();
        // sync mode returns { extracted, extractionError } — fail loud with the named reason.
        if (!body.Contains("\"extracted\":true"))
            throw new InvalidOperationException(
                $"source {source.Id} extraction failed — upload response: {body}");
    }

    /// <summary>Asks one question through the real /chat SSE endpoint; returns answer + citations + latency.</summary>
    public static async Task<QuestionResult> AskAsync(HttpClient client, EvalQuestion q, string? goldUrl)
    {
        var sw = Stopwatch.StartNew();
        var resp = await client.PostAsync("/chat",
            new StringContent(JsonSerializer.Serialize(new { message = q.Question }), Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();
        sw.Stop();

        var answer = new StringBuilder();
        var citations = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            var payload = line.Trim();
            if (!payload.StartsWith("data: ")) continue;
            var json = payload["data: ".Length..];
            if (json == "[DONE]") continue;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("text", out var t))
                answer.Append(t.GetString());
            if (root.TryGetProperty("citations", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var cit in c.EnumerateArray())
                    if (cit.TryGetProperty("url", out var u))
                        citations.Add(u.GetString()!);
        }
        return new QuestionResult(q.Id, q.Question, sw.Elapsed.TotalMilliseconds,
            answer.Length > 0, citations, answer.ToString());
    }

    public void Dispose()
    {
        _app?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
