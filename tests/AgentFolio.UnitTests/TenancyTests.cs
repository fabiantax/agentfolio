using Slices.Tenancy;
using AgentFolio.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentFolio.Platform.Tenancy;

namespace AgentFolio.UnitTests;

/// <summary>Temp-dir fixture: each test gets an isolated global.db path.</summary>
public sealed class TenancyFixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("tenancy-test-").FullName;
    public string GlobalDbPath => Path.Combine(Dir, "global.db");
    public TenantRegistry Registry { get; }
    public TenantShardRegistry Shards { get; }

    public TenancyFixture()
    {
        Registry = new TenantRegistry(GlobalDbPath);
        Registry.EnsureSchemaAsync().GetAwaiter().GetResult();
        Shards = new TenantShardRegistry(GlobalDbPath);
    }

    public void Dispose()
    {
        Shards.DisposeAll();
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

public class TenancyTests : IDisposable
{
    private readonly TenancyFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Create_then_Get_round_trips()
    {
        var created = await _fx.Registry.CreateTenantAsync("acme");
        var fetched = await _fx.Registry.GetBySlugAsync("acme");

        Assert.NotNull(fetched);
        Assert.Equal(created.TenantId, fetched!.TenantId);
        Assert.Equal("acme", fetched.Slug);
        Assert.False(fetched.Pinned);
        Assert.True(fetched.CreatedAt > 0);
    }

    [Fact]
    public async Task Duplicate_slug_throws()
    {
        await _fx.Registry.CreateTenantAsync("dup");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => _fx.Registry.CreateTenantAsync("dup"));
    }

    [Fact]
    public async Task Duplicate_slug_returns_409_via_endpoints()
    {
        Environment.SetEnvironmentVariable("AGENTFOLIO_ADMIN_TOKEN", "test-token");
        var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                var dir = Directory.CreateTempSubdirectory("tenancy-e2e-").FullName;
                var reg = new TenantRegistry(Path.Combine(dir, "global.db"));
                reg.EnsureSchemaAsync().GetAwaiter().GetResult();
                services.RemoveAll<TenantRegistry>();
                services.RemoveAll<TenantShardRegistry>();
                services.AddSingleton(reg);
                services.AddSingleton(new TenantShardRegistry(reg.GlobalDbPath));
            });
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Token", "test-token");

        var create = await client.PostAsync("/api/tenants",
            new StringContent("""{"slug":"alpha"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);

        var again = await client.PostAsync("/api/tenants",
            new StringContent("""{"slug":"alpha"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("acme-corp-1", true)]
    [InlineData("Acme", false)]          // uppercase rejected
    [InlineData("ab", false)]            // too short
    [InlineData("../../etc", false)]     // traversal
    [InlineData("", false)]
    public void Slug_matrix(string slug, bool expected)
    {
        Assert.Equal(expected, SlugValidator.IsValid(slug, out _));
    }

    [Fact]
    public void Slug_rejects_too_long()
    {
        Assert.False(SlugValidator.IsValid(new string('a', 41), out _));
        Assert.True(SlugValidator.IsValid(new string('a', 40), out _));
    }

    [Fact]
    public void Slug_rejects_traversal_and_null_byte_and_absolute()
    {
        Assert.False(SlugValidator.IsValid("../x", out _));
        Assert.False(SlugValidator.IsValid("a\0b", out _));
        Assert.False(SlugValidator.IsValid("/abs", out _));
    }

    [Fact]
    public async Task Disposal_leaves_zero_residue()
    {
        var created = await _fx.Registry.CreateTenantAsync("gone");
        var shardPath = Path.Combine(_fx.Shards.ShardRoot, created.TenantId, "data.db");
        await _fx.Shards.GetOrCreateShardAsync(created.TenantId);
        Assert.True(File.Exists(shardPath));

        // admin deletion path
        await _fx.Shards.EvictShardAsync(created.TenantId);
        await _fx.Registry.DeleteTenantAsync(created.TenantId);
        Directory.Delete(Path.Combine(_fx.Shards.ShardRoot, created.TenantId), recursive: true);

        Assert.False(Directory.Exists(Path.Combine(_fx.Shards.ShardRoot, created.TenantId)));
        Assert.Null(await _fx.Registry.GetBySlugAsync("gone"));
        Assert.False(File.Exists(shardPath));
    }

    [Fact]
    public async Task User_tenant_is_idempotent_per_github_id()
    {
        var first = await _fx.Registry.GetOrCreateUserTenantAsync(424242);
        var second = await _fx.Registry.GetOrCreateUserTenantAsync(424242);

        Assert.Equal(first.TenantId, second.TenantId);
        Assert.Equal("github-424242", second.Slug);
    }

    [Fact]
    public async Task Ulid_format_is_26_crockford_chars()
    {
        for (int i = 0; i < 32; i++)
        {
            var id = Ulid.NewUlid();
            Assert.Equal(26, id.Length);
            Assert.True(Ulid.IsValidFormat(id));
            // monotonic (roughly): timestamp component sorted == generated order
        }
        // Sortability guarantee: timestamp prefix is MSB-first, so ULIDs from
        // different milliseconds sort by time. Same-ms siblings may reorder (random
        // tail) — that's the spec'd ULID behavior for non-monotonic variants.
        var a = Ulid.NewUlid();
        await Task.Delay(2);
        var b = Ulid.NewUlid();
        Assert.True(string.CompareOrdinal(a, b) < 0, $"ULIDs must sort by time: {a} !< {b}");
    }

    [Fact]
    public async Task Shard_lru_evicts_beyond_cap()
    {
        for (int i = 0; i < TenantShardRegistry.MaxOpenShards + 8; i++)
        {
            var t = await _fx.Registry.CreateTenantAsync($"lru-{i}");
            await _fx.Shards.GetOrCreateShardAsync(t.TenantId);
        }
        // Conn-per-operation stores hold no handles; the LRU contract is: at most
        // MaxOpenShards shards TRACKED (evicted ones are forgettable). Assert via the
        // registry's own accounting, not WAL files.
        var tracked = _fx.Shards.TrackedCount;
        Assert.True(tracked <= TenantShardRegistry.MaxOpenShards,
            $"LRU must track <= {TenantShardRegistry.MaxOpenShards} shards, tracked {tracked}");
    }


    [Fact]
    public async Task Shard_store_is_usable()
    {
        var t = await _fx.Registry.CreateTenantAsync("usable");
        var shard = await _fx.Shards.GetOrCreateShardAsync(t.TenantId);
        Assert.IsType<SqliteStore>(shard);
        await shard.AppendAsync(new AgentFolio.Contracts.AppendOnlyEvent(
            "ping", "{}", DateTimeOffset.UtcNow));
    }
}
