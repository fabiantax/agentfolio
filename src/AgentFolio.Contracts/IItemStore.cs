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
    Task UpsertSaveAsync(SavedPostRecord post, CancellationToken ct = default);
    IAsyncEnumerable<StarredRepoRecord> QueryStarsAsync(CancellationToken ct = default);
    IAsyncEnumerable<SavedPostRecord> QuerySavesAsync(bool includeStale = false, CancellationToken ct = default);
}
