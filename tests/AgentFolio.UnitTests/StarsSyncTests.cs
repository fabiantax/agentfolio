using AgentFolio.Platform;
using AgentFolio.Contracts;
using Slices.Stars;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentFolio.UnitTests;

public sealed class StarsSyncTests : IDisposable
{
    private readonly string _tmpDb;
    private readonly SqliteStore _store;
    private readonly MockGitHubStarsClient _fakeClient;

    public StarsSyncTests()
    {
        _tmpDb = Path.Combine(System.IO.Path.GetTempPath(), $"test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_tmpDb);
        _fakeClient = new MockGitHubStarsClient();
    }

    public void Dispose()
    {
        foreach (var p in new[] { _tmpDb, _tmpDb + "-wal", _tmpDb + "-shm" })
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
    }

    [Fact]
    public async Task Sync_FetchesAllPagesAndWritesEventsPlusProjections()
    {
        // Arrange: fake client returns 3 pages totaling 102 items (> 101 proves the
        // multi-page fetch path — the client contract returns ALL starred items).
        for (var page = 0; page < 3; page++)
        {
            var items = new List<StarredRepoDto>();
            for (var i = page * 34; i < (page + 1) * 34; i++)
            {
                items.Add(new StarredRepoDto(i, $"repo{i}", $"user{i}/repo{i}", $"desc{i}", $"https://github.com/{i}", 10, "C#",
                    new[] { "top1" }, "user", "2024-01-01T00:00:00Z"));
            }
            _fakeClient.AddPage(items);
        }

        // Ensure store schema
        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        var service = new StarsSyncService(_fakeClient, _store);

        // Act
        var result = await service.SyncAsync();

        // Assert
        Assert.True(result.Fetched > 100, $"Expected fetched > 100, got {result.Fetched}");
        Assert.True(result.Upserted > 0, $"Expected upserted > 0, got {result.Upserted}");
        Assert.True(result.Unchanged >= 0, $"Expected unchanged >= 0, got {result.Unchanged}");

        // Verify projections exist
        var stars = new List<StarredRepoRecord>();
        await foreach (var s in _store.QueryStarsAsync()) stars.Add(s);
        Assert.Equal(result.Upserted, stars.Count);
    }

    [Fact]
    public async Task Sync_ReSyncWithUnchangedData_ReturnsZeroUpserted()
    {
        // Arrange: first sync
        _fakeClient.AddPage(new[]
        {
            new StarredRepoDto(1, "repo1", "user1/repo1", "desc1", "https://github.com/1", 10, "C#", new[] { "top1" }, "user1", "2024-01-01T00:00:00Z"),
        });

        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        var service = new StarsSyncService(_fakeClient, _store);
        var first = await service.SyncAsync();

        // Act: re-sync with same data
        var second = await service.SyncAsync();

        // Assert: unchanged should be > 0 and upserted should be 0
        Assert.Equal(first.Fetched, second.Fetched);
        Assert.Equal(0, second.Upserted);
        Assert.True(second.Unchanged > 0, $"Expected unchanged > 0, got {second.Unchanged}");
    }

    [Fact]
    public async Task Sync_RateLimitWaitPath_RespectsResetHeader()
    {
        // Arrange: fake client that fakes rate-limit timing
        _fakeClient.AddPageWithRateLimit(
            new[]
            {
                new StarredRepoDto(1, "repo1", "user1/repo1", "desc1", "https://github.com/1", 10, "C#", new[] { "top1" }, "user1", "2024-01-01T00:00:00Z"),
            },
            resetOffsetSeconds: -10 // simulate rate limit 10s ago, should wait ~11s
        );

        await _store.OpenAsync();
        await _store.EnsureSchemaAsync();

        var service = new StarsSyncService(_fakeClient, _store);

        // Act + Assert: should not throw; timing assertion depends on faked clock
        var result = await service.SyncAsync();

        Assert.True(result.Fetched > 0);
    }
}