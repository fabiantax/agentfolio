using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentFolio.Contracts;
using AgentFolio.Platform;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Slices.Stars;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentFolio.UnitTests;

[Collection("password-hash-env")]
public sealed class StarsEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"agentfolio-stars-ep-test-{Guid.NewGuid():N}.db");
    private readonly string _globalDbPath = Path.Combine(Path.GetTempPath(), $"agentfolio-stars-ep-global-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _globalDbPath, _globalDbPath + "-wal", _globalDbPath + "-shm" })
            if (File.Exists(p)) File.Delete(p);
        Environment.SetEnvironmentVariable("AGENTFOLIO_DB_PATH", null);
        Environment.SetEnvironmentVariable("AGENTFOLIO_GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar, null);
    }

    private static void AddFakeStarsPage(MockGitHubStarsClient fake)
    {
        fake.AddPage(new[]
        {
            new StarredRepoDto(1, "repo1", "user1/repo1", "desc1", "https://github.com/1", 10, "C#", new[] { "t1" }, "user1", "2024-03-01T00:00:00Z"),
            new StarredRepoDto(2, "repo2", "user2/repo2", "desc2", "https://github.com/2", 5, "Rust", new[] { "t2" }, "user2", "2024-06-01T00:00:00Z"),
        });
    }

    private WebApplicationFactory<Program> Factory(MockGitHubStarsClient? fake)
    {
        Environment.SetEnvironmentVariable("AGENTFOLIO_DB_PATH", _dbPath);
        Environment.SetEnvironmentVariable("AGENTFOLIO_GLOBAL_DB", _globalDbPath);
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar, PasswordGate.HashPassword("hunter2"));
        if (fake is not null)
            Environment.SetEnvironmentVariable("AGENTFOLIO_GITHUB_TOKEN", "test-token");

        // Ensure schemas on the temp DBs before requests hit the stores.
        var bootstrap = new SqliteStore(_dbPath);
        bootstrap.OpenAsync().GetAwaiter().GetResult();
        bootstrap.EnsureSchemaAsync().GetAwaiter().GetResult();
        var registry = new TenantRegistry(_globalDbPath);
        registry.EnsureSchemaAsync().GetAwaiter().GetResult();
        registry.CreateTenantAsync("showcase").GetAwaiter().GetResult();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.RemoveAll<TenantRegistry>();
                services.AddSingleton(registry);
                if (fake is not null)
                {
                    services.RemoveAll<IGitHubStarsClient>();
                    services.AddScoped<IGitHubStarsClient>(_ => fake);
                }
            });
        });
    }

    private static HttpClient LoggedInClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var login = client.PostAsJsonAsync("/auth/login", new { password = "hunter2" }).GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>202-contract helper: trigger, poll /stars/sync/status until terminal, return final status body.</summary>
    private static async Task<JsonElement> TriggerAndWaitAsync(HttpClient client, string path = "/stars/sync")
    {
        var response = await client.PostAsync(path, content: null);
        for (var tries = 0; response.StatusCode == HttpStatusCode.Conflict && tries < 20; tries++)
        {
            await Task.Delay(250); // a previous job still terminal-ing — retry trigger (client behavior)
            response = await client.PostAsync(path, content: null);
        }
        Assert.True(HttpStatusCode.Accepted == response.StatusCode,
            $"expected 202 got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(accepted.TryGetProperty("jobId", out _), "202 must carry jobId");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetAsync("/stars/sync/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            var body = await status.Content.ReadFromJsonAsync<JsonElement>();
            var state = body.GetProperty("status").GetString();
            if (state is "completed" or "failed")
            {
                Assert.True(state == "completed",
                    $"sync reached terminal state '{state}': {body.GetProperty("error").GetString()} " +
                    $"(pagesDone={body.GetProperty("pagesDone").GetInt32()}, lastPage={body.GetProperty("lastPageNote").GetString()})");
                return body;
            }
            await Task.Delay(100);
        }
        Assert.Fail("sync job did not reach a terminal state within 30s");
        return default;
    }

    [Fact]
    public async Task PostStarsSync_WithoutToken_Returns503GithubTokenNotConfigured()
    {
        await using var factory = Factory(fake: null);
        var client = LoggedInClient(factory);

        var response = await client.PostAsync("/stars/sync", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("github_token_not_configured", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostStarsSync_WithFakeClient_Returns200WithFetchedAndUpserted()
    {
        var fake = new MockGitHubStarsClient();
        AddFakeStarsPage(fake);
        await using var factory = Factory(fake);
        var client = LoggedInClient(factory);

        var body = await TriggerAndWaitAsync(client);

        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("fetched").GetInt32());
        Assert.True(body.GetProperty("upserted").GetInt32() > 0);
    }

    [Fact]
    public async Task GetStars_ReturnsSyncedReposOrderedByStarredAtDesc()
    {
        var fake = new MockGitHubStarsClient();
        AddFakeStarsPage(fake);
        await using var factory = Factory(fake);
        var client = LoggedInClient(factory);

        await TriggerAndWaitAsync(client);

        var response = await client.GetAsync("/stars");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stars = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, stars.GetArrayLength());
        var firstStar = stars[0];
        Assert.Equal("user2/repo2", firstStar.GetProperty("fullName").GetString());
        Assert.Equal(2, firstStar.GetProperty("repoId").GetInt64());
        var secondStar = stars[1];
        Assert.Equal("user1/repo1", secondStar.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task PostStarsSync_SecondSyncWithinTtl_HitsCacheWithoutRefetch()
    {
        var fake = new MockGitHubStarsClient();
        AddFakeStarsPage(fake);
        await using var factory = Factory(fake);
        var client = LoggedInClient(factory);

        var firstBody = await TriggerAndWaitAsync(client);
        Assert.Equal("completed", firstBody.GetProperty("status").GetString());

        var second = await client.PostAsync("/stars/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cache", secondBody.GetProperty("source").GetString());
        Assert.Equal(0, secondBody.GetProperty("fetched").GetInt32());
        Assert.Equal(2, secondBody.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public async Task PostStarsSync_ForceBypassesCacheAndRefetches()
    {
        var fake = new MockGitHubStarsClient();
        AddFakeStarsPage(fake);
        await using var factory = Factory(fake);
        var client = LoggedInClient(factory);

        await TriggerAndWaitAsync(client); // prime the snapshot

        var body = await TriggerAndWaitAsync(client, "/stars/sync?force=1");
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("fetched").GetInt32());
    }
}
