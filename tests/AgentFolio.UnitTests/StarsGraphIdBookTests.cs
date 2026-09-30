using Slices.Stars;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#66 B5: sidecar id-book — dense allocation across batches, ingested-cursor semantics.</summary>
public class StarsGraphIdBookTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"stars-idbook-{Guid.NewGuid():N}.db");
    private readonly AgentFolio.Platform.SqliteStore _store;

    public StarsGraphIdBookTests()
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
    public async Task Empty_book_next_id_equals_seed_floor()
    {
        Assert.Equal(123456L, await _store.StarsGraphNodeNextIdAsync(123456));
        Assert.Empty(await _store.StarsGraphNodeBookedIdsAsync());
    }

    [Fact]
    public async Task Booking_advances_next_id_contiguously_and_records_cursor()
    {
        // seed floor 200000 = the existing graph store's max node id (wireproof era)
        var next = await _store.StarsGraphNodeNextIdAsync(200_000);
        Assert.Equal(200_000L, next);

        await _store.StarsGraphNodeBookedAsync(75547, next, chunkCount: 2); // spans 200000..200002
        next = await _store.StarsGraphNodeNextIdAsync(200_000);
        Assert.Equal(200_003L, next);
        Assert.Equal(new long[] { 75547 }, await _store.StarsGraphNodeBookedIdsAsync());

        await _store.StarsGraphNodeBookedAsync(1393085110, next, chunkCount: 1); // 200003..200004
        next = await _store.StarsGraphNodeNextIdAsync(200_000);
        Assert.Equal(200_005L, next);

        // Re-book the same repo (idempotent upsert): updates in place, never duplicates.
        await _store.StarsGraphNodeBookedAsync(75547, next, chunkCount: 3);
        Assert.Equal(2, (await _store.StarsGraphNodeBookedIdsAsync()).Count); // still 2 repos, not 3
        Assert.Equal(200_009L, await _store.StarsGraphNodeNextIdAsync(200_000)); // 200005..200008
    }

    [Fact]
    public async Task Quarantine_accrues_at_three_strikes_and_resets_on_success()
    {
        // Two strikes: not quarantined yet.
        await _store.StarsGraphIngestFailedAsync(100);
        await _store.StarsGraphIngestFailedAsync(100);
        Assert.DoesNotContain(100L, await _store.StarsGraphIngestQuarantinedIdsAsync());

        // Third strike: quarantined.
        await _store.StarsGraphIngestFailedAsync(100);
        var q = await _store.StarsGraphIngestQuarantinedIdsAsync();
        Assert.Contains(100L, q);
        Assert.DoesNotContain(200L, q); // other repos unaffected

        // Success resets the counter entirely.
        await _store.StarsGraphIngestResetAsync(100);
        Assert.DoesNotContain(100L, await _store.StarsGraphIngestQuarantinedIdsAsync());

        // And the counter starts over from 1 after a reset.
        await _store.StarsGraphIngestFailedAsync(100);
        Assert.DoesNotContain(100L, await _store.StarsGraphIngestQuarantinedIdsAsync());
    }
}
