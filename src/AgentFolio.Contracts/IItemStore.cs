namespace AgentFolio.Contracts;

/// <summary>Projection read/write shape for a starred GitHub repo (upsert key: RepoId).</summary>
public sealed record StarredRepoRecord(
    long RepoId,
    string FullName,
    string? Description,
    string? TopicsJson,
    DateTimeOffset StarredAt);

/// <summary>Projection read/write shape for a saved LinkedIn post (upsert key: CanonicalUrl).</summary>
public sealed record SavedPostRecord(
    string CanonicalUrl,
    string? Title,
    string? TextExcerpt,
    bool IsStale,
    DateTimeOffset SavedAt);

/// <summary>Port: stars + saves projections, upsert-oriented. No event knowledge.</summary>
public interface IItemStore
{
    Task UpsertStarAsync(StarredRepoRecord repo, CancellationToken ct = default);

    /// <summary>Raw-first (#66): persist/refresh the FULL payload row. DIM: stores without raw support fail loud.</summary>
    Task UpsertStarRawAsync(long repoId, string fullPayloadJson, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>Raw-first (#66): persist README text for a repo's raw row.</summary>
    Task SetStarReadmeAsync(long repoId, string readmeText, DateTimeOffset fetchedAt, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>Raw-first (#66): ids of raw rows missing README (backfill cursor work).</summary>
    Task<System.Collections.Generic.IReadOnlyList<long>> GetStarIdsMissingReadmeAsync(int limit, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>Raw-first (#66): one raw row, or null.</summary>
    Task<StarredRepoRaw?> GetStarRawAsync(long repoId, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>#66 B5: record a repo's sidecar id allocation after a successful ingest.
    /// NodeIdBase = the graph node id of the repo node; chunk i lives at base+1+i.
    /// One row per graph-ingested repo — doubles as the ingest cursor (row exists = ingested).</summary>
    Task StarsGraphNodeBookedAsync(long repoId, long nodeIdBase, int chunkCount, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>#98 poison-repo quarantine: bump the consecutive-failure counter.
    /// The caller decides what "3 strikes" means; the store just persists the count
    /// (row auto-created, OnConflict upsert).</summary>
    Task StarsGraphIngestFailedAsync(long repoId, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>#98: repo ids with >=3 consecutive NER-stage failures (never booked).
    /// Selection excludes these until a success resets them.</summary>
    Task<System.Collections.Generic.ISet<long>> StarsGraphIngestQuarantinedIdsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>#98: clear a repo's failure strikes (called on successful ingest).</summary>
    Task StarsGraphIngestResetAsync(long repoId, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>#66 B5: repo ids already graph-ingested (the book's key set).</summary>
    Task<System.Collections.Generic.ISet<long>> StarsGraphNodeBookedIdsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    /// <summary>#66 B5: next free node id = max(booked span) + 1, lifted to seedFloor.
    /// GF sizes CSR/adjacency tables at O(max node id), so ids MUST stay dense.</summary>
    Task<long> StarsGraphNodeNextIdAsync(long seedFloor, CancellationToken ct = default)
        => throw new NotSupportedException("store has no stars_raw support");

    Task UpsertSaveAsync(SavedPostRecord post, CancellationToken ct = default);
    IAsyncEnumerable<StarredRepoRecord> QueryStarsAsync(CancellationToken ct = default);
    IAsyncEnumerable<SavedPostRecord> QuerySavesAsync(bool includeStale = false, CancellationToken ct = default);
}
