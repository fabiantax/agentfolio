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
    Task UpsertSaveAsync(SavedPostRecord post, CancellationToken ct = default);
    IAsyncEnumerable<StarredRepoRecord> QueryStarsAsync(CancellationToken ct = default);
    IAsyncEnumerable<SavedPostRecord> QuerySavesAsync(bool includeStale = false, CancellationToken ct = default);
}
