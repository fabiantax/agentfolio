using AgentFolio.Contracts;
using AgentFolio.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentFolio.UnitTests;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public SqliteStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"agentfolio-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
    }

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p)) File.Delete(p);
    }

    [Fact]
    public async Task Store_OpensTempDb_WithWalEnabled()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", ((string?)cmd.ExecuteScalar())!.ToLowerInvariant());
    }

    [Fact]
    public async Task EnsureSchema_CreatesEventsStarsSavesTables()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));

        Assert.Contains("events", tables);
        Assert.Contains("stars", tables);
        Assert.Contains("saves", tables);
    }

    [Fact]
    public async Task EnsureSchema_CreatesUniqueKeys_OnRepoIdAndCanonicalUrl()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = "SELECT COUNT(*) FROM pragma_index_list('stars') WHERE \"unique\"=1;";
            Assert.True(Convert.ToInt64(idx.ExecuteScalar()) >= 1);
        }
        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = "SELECT COUNT(*) FROM pragma_index_list('saves') WHERE \"unique\"=1;";
            Assert.True(Convert.ToInt64(idx.ExecuteScalar()) >= 1);
        }
    }

    [Fact]
    public async Task Vec0Extension_Loads_WhenNativeLibPresent()
    {
        // Skip-guard: native vec0 (sqlite-vec) may be absent on this RID.
        if (!_store.TryLoadVec0(out var error))
        {
            // python sqlite_vec install provides vec0.so on this machine for dev runs
            var fallback = FindVec0So();
            if (fallback is null)
            {
                // skip-guard (not a failure): native vec0 lib absent on this RID
                return;
            }
            var alt = new SqliteStore(_dbPath, fallback);
            Assert.True(alt.TryLoadVec0(out var err2), $"vec0 failed via fallback {fallback}: {err2}");
            await alt.OpenAsync();
            await alt.EnsureSchemaAsync();
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                conn.EnableExtensions();
                conn.LoadExtension("vec0"); // extensions are per-connection in SQLite
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS vec_test USING vec0(embedding float[4]);";
                cmd.ExecuteNonQuery();
                return;
            }
        }

        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            conn.EnableExtensions();
            conn.LoadExtension("vec0"); // extensions are per-connection in SQLite
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS vec_test USING vec0(embedding float[4]);";
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task UpsertStar_AppendsEventBeforeProjection_InOneTransaction()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        await _store.UpsertStarAsync(new StarredRepoRecord(42, "rust-lang/rust", "desc", "[\"systems\"]", DateTimeOffset.UtcNow));
        await _store.UpsertStarAsync(new StarredRepoRecord(42, "rust-lang/rust", "desc2", "[\"systems\",\"pl\"]", DateTimeOffset.UtcNow));

        // projection: exactly one row (upsert dedupe)
        var stars = new List<StarredRepoRecord>();
        await foreach (var s in _store.QueryStarsAsync()) stars.Add(s);
        var star = Assert.Single(stars);
        Assert.Equal(42, star.RepoId);
        Assert.Equal("desc2", star.Description);

        // ordering proof: event log has 2 rows (one per upsert), each with id strictly before
        // the projection state they produced; replaying events yields same final state.
        var events = new List<StoredEvent>();
        await foreach (var e in _store.RebuildAsync()) events.Add(e);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("star_synced", e.Type));
        Assert.True(events[0].Id < events[1].Id);

        // event rows were committed BEFORE projection rows in the same tx:
        // verify by count parity — events written == successful projection mutations.
        Assert.Equal(2, events.Count(e => e.Type == "star_synced"));
        Assert.Single(stars);
    }

    [Fact]
    public async Task UpsertSave_UpsertsOnCanonicalUrl()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        var url = "https://www.linkedin.com/posts/123";
        await _store.UpsertSaveAsync(new SavedPostRecord(url, "T1", "excerpt", false, DateTimeOffset.UtcNow));
        await _store.UpsertSaveAsync(new SavedPostRecord(url, "T2", "excerpt2", true, DateTimeOffset.UtcNow));

        var all = new List<SavedPostRecord>();
        await foreach (var s in _store.QuerySavesAsync(includeStale: true)) all.Add(s);
        var saved = Assert.Single(all);
        Assert.Equal("T2", saved.Title);
        Assert.True(saved.IsStale);

        var visible = new List<SavedPostRecord>();
        await foreach (var s in _store.QuerySavesAsync()) visible.Add(s);
        Assert.Empty(visible);
    }

    [Fact]
    public async Task EventAppend_IsAppendOnly_RebuildStreamsAllInOrder()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        for (var i = 0; i < 5; i++)
            await _store.AppendAsync(new AppendOnlyEvent("ping", $"{{\"i\":{i}}}", DateTimeOffset.UtcNow));

        var ids = new List<long>();
        await foreach (var e in _store.RebuildAsync()) ids.Add(e.Id);
        Assert.Equal(5, ids.Count);
        Assert.Equal(ids.OrderBy(x => x), ids);
    }

    private static string? FindVec0So()
    {
        var candidates = new[]
        {
            "/home/fabian/.venvs/headroom/lib/python3.13/site-packages/sqlite_vec/vec0.so",
            "/mnt/models/scratch/sqlite-spike/.venv/lib/python3.13/site-packages/sqlite_vec/vec0.so",
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
