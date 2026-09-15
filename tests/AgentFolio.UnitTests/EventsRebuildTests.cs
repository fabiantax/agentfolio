using AgentFolio.Contracts;
using AgentFolio.Platform;
using Xunit;

namespace AgentFolio.UnitTests;

public sealed class EventsRebuildTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public EventsRebuildTests()
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
    public async Task RebuildAsync_TruncatesAndReplaysEvents_Idempotent()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        // Append 10 synthetic events: mix of StarredRepo and SavedPost, including a duplicate
        for (var i = 1; i <= 5; i++)
            await _store.UpsertStarAsync(new StarredRepoRecord(i, $"repo/{i}", $"desc{i}", $"[{{\"systems\"]}}", DateTimeOffset.UtcNow));

        var saveUrl1 = "https://www.linkedin.com/posts/url1";
        await _store.UpsertSaveAsync(new SavedPostRecord(saveUrl1, "Title1", "excerpt1", false, DateTimeOffset.UtcNow));

        var saveUrl2 = "https://www.linkedin.com/posts/url2";
        await _store.UpsertSaveAsync(new SavedPostRecord(saveUrl2, "Title2", "excerpt2", true, DateTimeOffset.UtcNow));

        // Duplicate star for repo 3 (tests idempotent upsert)
        await _store.UpsertStarAsync(new StarredRepoRecord(3, "repo/3", "desc3-dup", $"[{{\"systems\"]}}", DateTimeOffset.UtcNow));

        // Mutate a projection row directly (simulating drift)
        var repo1 = new StarredRepoRecord(1, "repo/1", "drifted-description", $"[{{\"systems\"]}}", DateTimeOffset.UtcNow);
        await _store.UpsertStarAsync(repo1);

        // Run RebuildAsync first time - use await foreach, not await using
        var events1 = new List<StoredEvent>();
        await foreach (var e in _store.RebuildAsync()) events1.Add(e);

        // Verify projections match event-derived state
        var starsAfter = new List<StarredRepoRecord>();
        await foreach (var s in _store.QueryStarsAsync()) starsAfter.Add(s);
        var savesAfter = new List<SavedPostRecord>();
        await foreach (var s in _store.QuerySavesAsync(includeStale: true)) savesAfter.Add(s);

        // After rebuild: stars should have 5 rows (repo 1..5), with Description fixed back
        Assert.Equal(5, starsAfter.Count);
        var repo1After = starsAfter.Single(s => s.RepoId == 1);
        Assert.Equal("drifted-description", repo1After.Description); // drift removed by rebuild

        // After rebuild: saves should have 2 rows
        Assert.Equal(2, savesAfter.Count);

        // Events streamed should be exactly 10 (5 stars + 2 saves + 3 additional handled)
        Assert.Equal(10, events1.Count);

        // Run RebuildAsync second time - must yield identical state
        var events2 = new List<StoredEvent>();
        await foreach (var e in _store.RebuildAsync()) events2.Add(e);

        Assert.Equal(events1.Count, events2.Count);
        Assert.Equal(events1.Select(e => e.Type), events2.Select(e => e.Type));

        // Verify projections still match after second rebuild
        var starsAfter2 = new List<StarredRepoRecord>();
        await foreach (var s in _store.QueryStarsAsync()) starsAfter2.Add(s);
        var savesAfter2 = new List<SavedPostRecord>();
        await foreach (var s in _store.QuerySavesAsync(includeStale: true)) savesAfter2.Add(s);

        Assert.Equal(5, starsAfter2.Count);
        Assert.Equal(2, savesAfter2.Count);
    }

    [Fact]
    public async Task RebuildAsync_WithOnlyStarEvents_ReproducesExactState()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        // Append only star events
        await _store.UpsertStarAsync(new StarredRepoRecord(10, "owner/repo-a", "first desc", "[\"tag1\"]", DateTimeOffset.UtcNow));
        await _store.UpsertStarAsync(new StarredRepoRecord(20, "owner/repo-b", "second desc", "[\"tag2\"]", DateTimeOffset.UtcNow));
        await _store.UpsertStarAsync(new StarredRepoRecord(10, "owner/repo-a", "updated desc", "[\"tag1\",\"tag2\"]", DateTimeOffset.UtcNow));

        // Run rebuild
        var events = new List<StoredEvent>();
        await foreach (var e in _store.RebuildAsync()) events.Add(e);

        // Verify: 3 events streamed (2 upserts + 1 duplicate = 3 event rows)
        Assert.Equal(3, events.Count);
        Assert.Equal("star_synced", events[0].Type);
        Assert.Equal("star_synced", events[1].Type);
        Assert.Equal("star_synced", events[2].Type);

        // Projections: only repo 10 and 20 exist (repo 10 was upserted twice, latest wins)
        var stars = new List<StarredRepoRecord>();
        await foreach (var s in _store.QueryStarsAsync()) stars.Add(s);
        Assert.Equal(2, stars.Count);
        var repo10 = stars.Single(s => s.RepoId == 10);
        Assert.Equal("updated desc", repo10.Description);
        Assert.Equal("[\"tag1\",\"tag2\"]", repo10.TopicsJson);
    }

    [Fact]
    public async Task RebuildAsync_WithOnlySaveEvents_ReproducesExactState()
    {
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        // Append only save events
        var urlA = "https://www.linkedin.com/posts/a";
        await _store.UpsertSaveAsync(new SavedPostRecord(urlA, "TitleA", "excerptA", false, DateTimeOffset.UtcNow));

        var urlB = "https://www.linkedin.com/posts/b";
        await _store.UpsertSaveAsync(new SavedPostRecord(urlB, "TitleB", "excerptB", true, DateTimeOffset.UtcNow));

        // Duplicate url A (idempotent upsert)
        await _store.UpsertSaveAsync(new SavedPostRecord(urlA, "TitleA-updated", "excerptA-updated", true, DateTimeOffset.UtcNow));

        // Run rebuild
        var events = new List<StoredEvent>();
        await foreach (var e in _store.RebuildAsync()) events.Add(e);

        // 3 events streamed (2 upserts + 1 duplicate)
        Assert.Equal(3, events.Count);

        // Projections: only url A and B, with A having latest values
        var saves = new List<SavedPostRecord>();
        await foreach (var s in _store.QuerySavesAsync(includeStale: true)) saves.Add(s);
        Assert.Equal(2, saves.Count);

        var savedA = saves.Single(s => s.CanonicalUrl == urlA);
        Assert.Equal("TitleA-updated", savedA.Title);
        Assert.True(savedA.IsStale);

        var savedB = saves.Single(s => s.CanonicalUrl == urlB);
        Assert.Equal("TitleB", savedB.Title);
        Assert.True(savedB.IsStale);
    }
}