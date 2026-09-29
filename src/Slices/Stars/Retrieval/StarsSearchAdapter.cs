using AgentFolio.Contracts;

namespace Slices.Stars.Retrieval;

/// <summary>
/// Chat-facing adapter over the stage-C retrieval stack (A/B winner arm D, 9/10:
/// GF recall top-N then ColBERT rerank over enriched docs). Degrade-open per
/// IStarsSearch contract: any retrieval failure yields an empty list — chat never
/// fails because search did. Implemented here, wired via Program.cs DI; Chat only
/// sees AgentFolio.Contracts.IStarsSearch (no slice-slice references).
/// </summary>
public sealed class StarsSearchAdapter : IStarsSearch
{
    private readonly StarsRetrievalClient _client;
    private readonly ColbertReranker _reranker;
    private readonly int _recallN;

    public StarsSearchAdapter(StarsRetrievalClient client, ColbertReranker reranker, int recallN = 30)
    {
        _client = client;
        _reranker = reranker;
        _recallN = recallN;
    }

    public async Task<IReadOnlyList<StarsSearchHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        try
        {
            // Recall ladder (#87): chunk Bm25 first (richest text); Repo keyword
            // recall when the graph has no chunk rows yet (a2a write gate) — both
            // legs are GF queries, same result contract, arm A + arm D of the A/B gate.
            var (hits, _) = await _client.SearchWithRerankAsync(
                RetrievalStrategy.Bm25, query, _reranker, topK, _recallN, ct).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                hits = (await _client.SearchWithRerankAsync(
                    RetrievalStrategy.Repo, query, _reranker, topK, _recallN, ct).ConfigureAwait(false)).Hits;
            }
            return hits
                .Where(h => !string.IsNullOrWhiteSpace(h.RepoFullName) || h.RepoId is not null)
                .Select(h => new StarsSearchHit(
                    h.RepoFullName ?? $"repo:{h.RepoId}",
                    h.Text,
                    h.RerankText,
                    h.RepoId))
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // GF or ColBERT down: uncited answer beats a failed one (A/B harness contract).
            return Array.Empty<StarsSearchHit>();
        }
    }
}
