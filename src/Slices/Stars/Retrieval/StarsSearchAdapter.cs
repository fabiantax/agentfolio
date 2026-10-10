using AgentFolio.Contracts;

namespace Slices.Stars.Retrieval;

/// <summary>
/// Chat-facing adapter over the stage-C retrieval stack. Recall ladder (winner
/// FIRST — preview-gate #123): EnrichedRecall (arm D, the measured 9/10 winner:
/// harness-verbatim keyword recall over stars_raw enrichment, hits carry
/// RerankText) → GF Bm25 (arm B) → GF Repo keyword recall (arm A). ColBERT
/// reranks every arm's candidates over RerankText ?? Text. Degrade-open per
/// IStarsSearch contract: any retrieval failure yields an empty list — chat never
/// fails because search did. Implemented here, wired via Program.cs DI; Chat only
/// sees AgentFolio.Contracts.IStarsSearch (no slice-slice references).
/// </summary>
public sealed class StarsSearchAdapter : IStarsSearch
{
    private readonly StarsRetrievalClient _client;
    private readonly ColbertReranker _reranker;
    private readonly EnrichedRecallProvider? _enriched;
    private readonly int _recallN;

    public StarsSearchAdapter(
        StarsRetrievalClient client,
        ColbertReranker reranker,
        int recallN = 30,
        EnrichedRecallProvider? enrichedRecall = null)
    {
        _client = client;
        _reranker = reranker;
        _enriched = enrichedRecall;
        _recallN = recallN;
    }

    public async Task<IReadOnlyList<StarsSearchHit>> SearchAsync(
        string query, int topK = 5, CancellationToken ct = default)
    {
        try
        {
            // Winner arm first (#123): enriched-doc recall from the tenant's own
            // stars store. Unpopulated store or no keyword hits → the GF ladder.
            if (_enriched is not null)
            {
                var (enriched, _) = await RerankAsync(
                    await _enriched.RecallAsync(query, _recallN, ct).ConfigureAwait(false),
                    query, topK, ct).ConfigureAwait(false);
                if (enriched.Count > 0)
                    return ToContract(enriched);
            }

            // GF ladder: chunk Bm25 (richest text) → Repo keyword recall (both GF
            // queries, same result contract; arms A + B of the A/B gate).
            var (bm25, _) = await RerankAsync(
                await _client.SearchAsync(RetrievalStrategy.Bm25, query, _recallN, ct).ConfigureAwait(false),
                query, topK, ct).ConfigureAwait(false);
            if (bm25.Count == 0)
            {
                bm25 = (await RerankAsync(
                    await _client.SearchAsync(RetrievalStrategy.Repo, query, _recallN, ct).ConfigureAwait(false),
                    query, topK, ct).ConfigureAwait(false)).Hits;
            }
            return ToContract(bm25);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // GF or ColBERT down: uncited answer beats a failed one (A/B harness contract).
            return Array.Empty<StarsSearchHit>();
        }
    }

    private async Task<(IReadOnlyList<RetrievalHit> Hits, bool Reranked)> RerankAsync(
        IReadOnlyList<RetrievalHit> candidates, string query, int topK, CancellationToken ct)
        => await _reranker.RerankAsync(query, candidates, topK, ct).ConfigureAwait(false);

    private static IReadOnlyList<StarsSearchHit> ToContract(IReadOnlyList<RetrievalHit> hits)
        => hits
            .Where(h => !string.IsNullOrWhiteSpace(h.RepoFullName) || h.RepoId is not null)
            .Select(h => new StarsSearchHit(
                h.RepoFullName ?? $"repo:{h.RepoId}",
                h.Text,
                h.RerankText,
                h.RepoId))
            .ToList();
}
