using AgentFolio.Contracts;
using Slices.Stars;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#70 A1: stars_raw roundtrip + idempotent re-upsert (rebuild-determinism precondition).</summary>
public class StarsRawStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"stars-raw-{Guid.NewGuid():N}.db");
    private readonly AgentFolio.Platform.SqliteStore _store;

    public StarsRawStoreTests()
    {
        _store = new AgentFolio.Platform.SqliteStore(_dbPath);
        _store.OpenAsync().GetAwaiter().GetResult();
        _store.EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p)) File.Delete(p);
    }

    [Fact]
    public async Task Upsert_then_get_roundtrips_full_payload()
    {
        await _store.UpsertStarRawAsync(42, """{"id":42,"full_name":"a/b","description":"d"}""");
        var raw = await _store.GetStarRawAsync(42);
        Assert.NotNull(raw);
        Assert.Equal(42, raw!.RepoId);
        Assert.Contains("a/b", raw.FullPayloadJson);
        Assert.Null(raw.ReadmeText);

        await _store.SetStarReadmeAsync(42, "# readme", DateTimeOffset.UtcNow);
        raw = await _store.GetStarRawAsync(42);
        Assert.Equal("# readme", raw!.ReadmeText);
        Assert.NotNull(raw.ReadmeFetchedAt);
    }

    [Fact]
    public async Task Reupsert_preserves_readme_and_is_idempotent()
    {
        await _store.UpsertStarRawAsync(7, "{\"id\":7}");
        await _store.SetStarReadmeAsync(7, "r", DateTimeOffset.UtcNow);
        await _store.UpsertStarRawAsync(7, "{\"id\":7,\"pushed_at\":\"x\"}");
        var raw = await _store.GetStarRawAsync(7);
        Assert.Equal("r", raw!.ReadmeText);              // README survives metadata refresh
        Assert.Contains("pushed_at", raw.FullPayloadJson); // payload refreshed
    }

    [Fact]
    public async Task Missing_readme_ids_returns_backfill_cursor()
    {
        await _store.UpsertStarRawAsync(1, "{}");
        await _store.UpsertStarRawAsync(2, "{}");
        await _store.SetStarReadmeAsync(2, "x", DateTimeOffset.UtcNow);
        var ids = await _store.GetStarIdsMissingReadmeAsync(10);
        Assert.Equal(new long[] { 1 }, ids);
    }

    [Fact]
    public async Task ParseStarredPage_emits_raw_per_repo()
    {
        const string page = """
        [
          {"starred_at":"2026-01-01T00:00:00Z","repo":{"id":5,"name":"n","full_name":"o/n","description":null,
           "html_url":"https://github.com/o/n","stargazers_count":1,"language":"C#","topics":[],
           "owner":{"login":"o"}}}
        ]
        """;
        var raws = new Dictionary<long, string>();
        var dtos = HttpGitHubStarsClient.ParseStarredPage(page, (id, json) => raws[id] = json);
        Assert.Single(dtos);
        Assert.True(raws.ContainsKey(5));
        Assert.Contains("\"full_name\":\"o/n\"", raws[5]);
    }
}
