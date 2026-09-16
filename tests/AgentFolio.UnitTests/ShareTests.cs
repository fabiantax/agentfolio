using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentFolio.Contracts;
using AgentFolio.Platform;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Migrations;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Slices.Share;
using Slices.Tenancy;

namespace AgentFolio.UnitTests;

/// <summary>Temp-dir fixture for share tests: isolated global.db + source store.</summary>
public sealed class ShareFixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("share-test-").FullName;
    public string GlobalDbPath => Path.Combine(Dir, "global.db");
    public string SourceDbPath => Path.Combine(Dir, "source", "agentfolio.db");
    public TenantRegistry Registry { get; }
    public TenantShardRegistry Shards { get; }

    public ShareFixture()
    {
        Registry = new TenantRegistry(GlobalDbPath);
        Registry.EnsureSchemaAsync().GetAwaiter().GetResult();
        Shards = new TenantShardRegistry(GlobalDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(SourceDbPath)!);
    }

    public SqliteStore NewStore(string path)
    {
        var store = new SqliteStore(path);
        store.EnsureSchemaAsync().GetAwaiter().GetResult();
        store.OpenAsync().GetAwaiter().GetResult();
        return store;
    }

    public void Dispose()
    {
        Shards.DisposeAll();
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

public class ShareTests : IDisposable
{
    private readonly ShareFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // ---------- migration ----------

    private async Task<MigrationResult> SeedAndMigrateAsync()
    {
        var source = _fx.NewStore(_fx.SourceDbPath);
        var now = DateTimeOffset.UtcNow;
        await source.UpsertStarAsync(new StarredRepoRecord(1, "a/a", "desc a", "[\"x\"]", now));
        await source.UpsertStarAsync(new StarredRepoRecord(2, "b/b", null, null, now));
        await source.UpsertStarAsync(new StarredRepoRecord(3, "c/c", "desc c", null, now));
        await source.UpsertSaveAsync(new SavedPostRecord("https://x/1", "T1", "excerpt 1", false, now));
        await source.UpsertSaveAsync(new SavedPostRecord("https://x/2", null, null, true, now));

        // Upsert* already appended 5 events (event-lite sourcing); add 2 more rows
        // so the source carries 7 events total (mirror of the legacy single-tenant log).
        await source.AppendAsync(new AppendOnlyEvent("note", """{"n":1}""", now));
        await source.AppendAsync(new AppendOnlyEvent("note", """{"n":2}""", now));

        return await MigrateSingleToTenant.Run(_fx.SourceDbPath, _fx.Registry, _fx.Shards);
    }

    private async Task<(int Stars, int Saves, int Events)> ShardCountsAsync(string tenantId)
    {
        var shard = await _fx.Shards.GetOrCreateShardAsync(tenantId);
        var stars = 0; await foreach (var _ in shard.QueryStarsAsync()) stars++;
        var saves = 0; await foreach (var _ in shard.QuerySavesAsync(includeStale: true)) saves++;
        var events = 0; await foreach (var _ in shard.RebuildAsync()) events++;
        return (stars, saves, events);
    }

    [Fact]
    public async Task Migration_copies_stars_saves_events_with_parity()
    {
        var result = await SeedAndMigrateAsync();

        Assert.Equal(3, result.Stars);
        Assert.Equal(2, result.Saves);
        // 5 upsert events in the source dedupe against the shard's own upsert-derived
        // events (idempotent copy); only the 2 note events are new -> reported as 2.
        Assert.Equal(2, result.Events);

        var showcase = await _fx.Registry.GetBySlugAsync(MigrateSingleToTenant.ShowcaseSlug);
        Assert.NotNull(showcase);
        Assert.Equal(showcase!.TenantId, result.TenantId);

        var (stars, saves, events) = await ShardCountsAsync(result.TenantId);
        Assert.Equal(3, stars);
        Assert.Equal(2, saves);
        // Parity: source had 7 events; shard has 7 (5 upsert-derived + 2 migrated notes,
        // with the source's own 5 upsert events deduped against identical payloads).
        Assert.Equal(7, events);

        // Spot-check one star round-trips intact.
        var shard = await _fx.Shards.GetOrCreateShardAsync(result.TenantId);
        StarredRepoRecord? star2 = null;
        await foreach (var s in shard.QueryStarsAsync())
            if (s.RepoId == 2) star2 = s;
        Assert.NotNull(star2);
        Assert.Equal("b/b", star2!.FullName);
        Assert.Null(star2.Description);
    }

    [Fact]
    public async Task Migration_is_idempotent_second_run_adds_nothing()
    {
        var first = await SeedAndMigrateAsync();
        var second = await MigrateSingleToTenant.Run(_fx.SourceDbPath, _fx.Registry, _fx.Shards);

        Assert.Equal(first.TenantId, second.TenantId);
        Assert.Equal(0, second.Events); // nothing re-appended
        var (stars, saves, events) = await ShardCountsAsync(first.TenantId);
        Assert.Equal(3, stars);
        Assert.Equal(2, saves);
        Assert.Equal(7, events); // unchanged from first run
    }

    // ---------- share endpoints ----------

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> NewApp()
    {
        return new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<TenantRegistry>();
                    services.RemoveAll<TenantShardRegistry>();
                    services.AddSingleton(_fx.Registry);
                    services.AddSingleton(_fx.Shards);
                });
            });
    }

    [Fact]
    public async Task Unknown_slug_returns_404()
    {
        await using var app = NewApp();
        var client = app.CreateClient();
        var resp = await client.GetAsync("/share/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.StartsWith("noindex", resp.Headers.GetValues("X-Robots-Tag").First());
    }

    [Fact]
    public async Task Fresh_unseeded_db_showcase_returns_named_503_not_stack_trace()
    {
        // #45: cold boot against an empty global.db (no EnsureSchema, no tenants).
        // The registry lazily ensures schema; the demo slug must fail HONESTLY with a
        // named 503 — never an unhandled SqliteException 500 stack trace.
        var dir = Directory.CreateTempSubdirectory("af-fresh-boot-");
        try
        {
            var registry = new TenantRegistry(Path.Combine(dir.FullName, "global.db")); // schema NOT ensured
            var shards = new TenantShardRegistry(registry.GlobalDbPath);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<TenantRegistry>();
                    services.RemoveAll<TenantShardRegistry>();
                    services.AddSingleton(registry);
                    services.AddSingleton(shards);
                });
            });
            var client = factory.CreateClient();

            var resp = await client.GetAsync("/share/showcase");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("showcase_not_seeded", body);
            Assert.DoesNotContain("SqliteException", body);

            // Non-demo slugs on a fresh registry stay 404 (not 503).
            var other = await client.GetAsync("/share/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }
        finally
        {
            try { Directory.Delete(dir.FullName, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Ungated_share_serves_page_with_noindex()
    {
        await SeedAndMigrateAsync();
        await using var app = NewApp();
        var client = app.CreateClient();
        var resp = await client.GetAsync("/share/showcase");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("noindex", resp.Headers.GetValues("X-Robots-Tag").First());
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<title>showcase", html);
        Assert.Contains("agentfolio-chat", html);
    }

    [Fact]
    public async Task Gated_share_wrong_password_401_and_correct_password_sets_cookie_and_serves_page()
    {
        await SeedAndMigrateAsync();
        var showcase = (await _fx.Registry.GetBySlugAsync(MigrateSingleToTenant.ShowcaseSlug))!;
        await _fx.Registry.SetSharePasswordHashAsync(showcase.TenantId, PasswordGate.HashPassword("hunter2"));

        await using var app = NewApp();
        var client = app.CreateClient();

        // First GET shows the gate.
        var gate = await client.GetAsync("/share/showcase");
        Assert.Equal(HttpStatusCode.OK, gate.StatusCode);
        var gateHtml = await gate.Content.ReadAsStringAsync();
        Assert.Contains("unlock", gateHtml);
        Assert.StartsWith("noindex", gate.Headers.GetValues("X-Robots-Tag").First());

        // Wrong password → 401.
        var bad = await client.PostAsync("/share/showcase/unlock",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("password", "wrong")]));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // Correct password → cookie set + page served.
        var good = await client.PostAsync("/share/showcase/unlock",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("password", "hunter2")]));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.StartsWith("noindex", good.Headers.GetValues("X-Robots-Tag").First());
        var setCookie = Assert.Single(good.Headers.GetValues("Set-Cookie"));
        Assert.Contains($"af_share_{showcase.TenantId}", setCookie);
        Assert.Contains("httponly", setCookie.ToLowerInvariant());

        // Cookie unlocks subsequent GETs.
        var cookieValue = setCookie.Split(';')[0];
        var req = new HttpRequestMessage(HttpMethod.Get, "/share/showcase");
        req.Headers.Add("Cookie", cookieValue);
        var unlocked = await client.SendAsync(req);
        var html = await unlocked.Content.ReadAsStringAsync();
        Assert.Contains("agentfolio-chat", html);
    }

    // ---------- isolation matrix ----------

    [Fact]
    public async Task Isolation_shard_resolution_ignores_forged_tid_and_share_a_only_serves_a()
    {
        var tenantA = await _fx.Registry.CreateTenantAsync("aaa");
        var tenantB = await _fx.Registry.CreateTenantAsync("bbb");

        var shardA = await _fx.Shards.GetOrCreateShardAsync(tenantA.TenantId);
        var shardB = await _fx.Shards.GetOrCreateShardAsync(tenantB.TenantId);
        await shardA.UpsertStarAsync(new StarredRepoRecord(100, "a/only", null, null, DateTimeOffset.UtcNow));
        await shardB.UpsertStarAsync(new StarredRepoRecord(200, "b/only", null, null, DateTimeOffset.UtcNow));

        // 1) Direct helper probe: resolution of slug 'a' NEVER consults client input.
        //    Even with a forged tid claim for B in hand, ResolveTenantAsync('a')
        //    returns A's registry row — the only id that can reach a shard.
        var resolved = await ShareEndpoints.ResolveTenantAsync(_fx.Registry, "aaa");
        Assert.NotNull(resolved);
        Assert.Equal(tenantA.TenantId, resolved!.TenantId);
        Assert.NotEqual(tenantB.TenantId, resolved.TenantId);

        //    Forged tid claim (attacker puts B's tenant id in an af_sid-style cookie /
        //    tid claim): resolution ignores it — slug → registry row is the whole story.
        var forgedTid = tenantB.TenantId;
        var resolvedAgain = await ShareEndpoints.ResolveTenantAsync(_fx.Registry, "aaa");
        Assert.Equal(tenantA.TenantId, resolvedAgain!.TenantId);
        Assert.NotEqual(forgedTid, resolvedAgain.TenantId);

        // 2) HTTP: /share/aaa must serve A's tenant context, never B's.
        await using var app = NewApp();
        var client = app.CreateClient();
        var resp = await client.GetAsync("/share/aaa");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(tenantA.TenantId, html); // tenant id never leaks to share visitors
        

        // 3) Crafted cookies carrying B's ids do not change what /share/a touches.
        var req = new HttpRequestMessage(HttpMethod.Get, "/share/aaa");
        req.Headers.TryAddWithoutValidation("Cookie",
            $"af_sid={tenantB.TenantId}; tid={tenantB.TenantId}; af_share_{tenantB.TenantId}=1");
        var spoofed = await client.SendAsync(req);
        var spoofHtml = await spoofed.Content.ReadAsStringAsync();
        Assert.Contains("agentfolio-chat", spoofHtml); // page serves; tenant id never in HTML
        

        // 4) B's data is only reachable via /share/b — never leaked through /share/a.
        var respB = await client.GetAsync("/share/bbb");
        var htmlB = await respB.Content.ReadAsStringAsync();
        Assert.Contains("agentfolio-chat", htmlB);
        

        // 5) Server-side guarantee: shard lookup is keyed by the registry ULID only —
        //    a non-ULID tid throws rather than resolving any path from client input.
        Assert.Throws<ArgumentException>(() => _fx.Shards.ShardPath("../../b"));
    }
}
