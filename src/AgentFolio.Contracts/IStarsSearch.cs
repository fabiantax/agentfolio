namespace AgentFolio.Contracts;

/// <summary>
/// Stars retrieval for chat (#66 stage C->D): GF recall + ColBERT rerank over the
/// starred-repos corpus. Implemented in Slices.Stars (adapter), consumed by Chat
/// via DI — slices never reference slices, the composition root wires them.
/// Degrade-open: implementers return an empty list when retrieval is unavailable;
/// chat proceeds uncited, never fails on retrieval.
/// </summary>
public interface IStarsSearch
{
    Task<IReadOnlyList<StarsSearchHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default);
}

/// <summary>One cited repo hit (citation contract for chat answers).</summary>
public sealed record StarsSearchHit(
    string FullName,
    string Text,
    string? RerankText,
    long? RepoId);
