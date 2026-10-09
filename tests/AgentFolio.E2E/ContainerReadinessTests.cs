using System.Net;
using System.Net.Http.Json;
using AgentFolio.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentFolio.E2E;

/// <summary>
/// #100 container-readiness contract:
/// - /healthz answers 200 in the real pipeline (in-container O3 smoke uses it);
/// - ProductionStartupValidator: Production with missing required env fails LOUDLY
///   at startup naming every missing variable (AGENTS.md rule 1, #100 AC);
/// - Production with the full env set boots and serves (fail-loud has no false positives).
/// The validator is a static gate on IsProduction — these drive it through the REAL
/// WebApplicationFactory pipeline with Environment=Production and env restored in finally.
/// </summary>
public class ContainerReadinessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("af-#100-").FullName;
    private readonly (string Name, string? Old) _saved = new(string.Empty, null);
    private readonly List<(string Name, string? Old)> _savedVars = [];

    [Fact]
    public async Task Healthz_returns_200_with_status_ok()
    {
        var (app, client) = TestHost();
        using var resp = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("ok", body!["status"]);
        app.Dispose();
    }

    [Fact]
    public void Production_with_missing_required_env_fails_loudly_naming_all_missing()
    {
        // Wipe every required variable -> the factory build must throw naming them ALL.
        foreach (var name in ProductionStartupValidator.RequiredVariables)
        {
            _savedVars.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, null);
        }
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                using var app = NewProductionFactory();
                _ = app.Services; // forces host build -> validator fires
            });
            foreach (var name in ProductionStartupValidator.RequiredVariables)
                Assert.Contains(name, ex.Message);
        }
        finally
        {
            foreach (var (name, old) in _savedVars)
                Environment.SetEnvironmentVariable(name, old);
        }
    }

    [Fact]
    public async Task Production_with_full_required_env_boots_and_serves_healthz()
    {
        // All required vars present (values need not be REACHABLE — this is a startup
        // contract test, not a dependency test) -> host builds, /healthz serves.
        // S3 env is included too: Program.cs's own Production upload gate (rule 1)
        // fires before the pipeline finishes — the validator is not its replacement.
        foreach (var name in ProductionStartupValidator.RequiredVariables)
        {
            _savedVars.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, $"http://test.invalid/{name.ToLowerInvariant()}");
        }
        // GLOBAL_DB must be a WRITABLE path (real registries open it at startup);
        // the other backend URLs are startup-contract placeholders only.
        var globalDb = Path.Combine(_dir, "global.db");
        Environment.SetEnvironmentVariable("AGENTFOLIO_GLOBAL_DB", globalDb);
        _savedVars.Add(("AGENTFOLIO_GLOBAL_DB", null));
        _savedVars.Add(("AGENTFOLIO_S3_ENDPOINT", Environment.GetEnvironmentVariable("AGENTFOLIO_S3_ENDPOINT")));
        _savedVars.Add(("AGENTFOLIO_S3_BUCKET", Environment.GetEnvironmentVariable("AGENTFOLIO_S3_BUCKET")));
        _savedVars.Add(("AGENTFOLIO_S3_ACCESS_KEY", Environment.GetEnvironmentVariable("AGENTFOLIO_S3_ACCESS_KEY")));
        _savedVars.Add(("AGENTFOLIO_S3_SECRET_KEY", Environment.GetEnvironmentVariable("AGENTFOLIO_S3_SECRET_KEY")));
        _savedVars.Add(("AGENTFOLIO_S3_REGION", Environment.GetEnvironmentVariable("AGENTFOLIO_S3_REGION")));
        Environment.SetEnvironmentVariable("AGENTFOLIO_S3_ENDPOINT", "http://test.invalid/s3");
        Environment.SetEnvironmentVariable("AGENTFOLIO_S3_BUCKET", "test-bucket");
        Environment.SetEnvironmentVariable("AGENTFOLIO_S3_ACCESS_KEY", "test-key");
        Environment.SetEnvironmentVariable("AGENTFOLIO_S3_SECRET_KEY", "test-secret");
        Environment.SetEnvironmentVariable("AGENTFOLIO_S3_REGION", "test-region");
        try
        {
            using var app = NewProductionFactory();
            var client = app.CreateClient();
            using var resp = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            foreach (var (name, old) in _savedVars)
                Environment.SetEnvironmentVariable(name, old);
        }
    }

    public void Dispose()
    {
        // Belt and braces: never leak Production env into sibling tests.
        foreach (var (name, old) in _savedVars)
            Environment.SetEnvironmentVariable(name, old);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WebApplicationFactory<Program> NewProductionFactory()
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment(Environments.Production));

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App, HttpClient Client) TestHost()
    {
        var app = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        return (app, app.CreateClient());
    }
}
