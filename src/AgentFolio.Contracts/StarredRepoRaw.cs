namespace AgentFolio.Contracts;

/// <summary>
/// Raw-first source-of-truth row for one starred repo (#66): the FULL GitHub
/// payload as returned, plus README once backfilled. Derived views (projections,
/// chunks, embeddings, graph) are rebuildable from this — never mutate raw.
/// </summary>
public sealed record StarredRepoRaw(
    long RepoId,
    string FullPayloadJson,
    string? ReadmeText,
    DateTimeOffset? ReadmeFetchedAt,
    DateTimeOffset FetchedAt);
