namespace AgentFolio.Contracts;

/// <summary>
/// One enriched stars row (preview-gate #123): the recall/rerank hay for the
/// stage-C winner arm — description + topics + use_for + problems per repo,
/// read from the tenant's own stars_raw table joined to the stars projection.
/// </summary>
public sealed record StarsEnrichedRow(
    long RepoId,
    string FullName,
    string? Description,
    string? TopicsJson,
    string? UseFor,
    string? ProblemsJson);
