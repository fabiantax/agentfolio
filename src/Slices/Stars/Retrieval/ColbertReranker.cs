namespace Slices.Stars.Retrieval;

/// <summary>
/// ColBERT late-interaction reranker client (#66 stage C2). Backed by the local
/// colbert-rerank.service (:8092, jinaai/jina-colbert-v2 via PyLate, MaxSim scoring).
/// Contract measured live 2026-09-28: POST /rerank {query, documents:[{text}]}
/// -> {results:[{index, relevance_score}...]} best-first.
/// Pipeline position: GF hybrid returns top-N candidates (recall), ColBERT re-orders
/// (precision). SOTA check: late-interaction MaxSim is the current reranking SOTA
/// family (answerai-colbert-small / jina-colbert-v2); cross-encoders (Qwen3-reranker)
/// score higher on BEIR but cost O(query x doc) transformer passes — the deployed
/// service wins the latency budget for a low-QPS demo.
/// </summary>
public class ColbertReranker
{
    private readonly HttpClient _http;

    public ColbertReranker(string baseUrl = "http://127.0.0.1:8092", int timeoutSeconds = 60)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    /// <summary>
    /// Rerank candidates best-first. Falls back to the input order (with a flag)
    /// when the reranker is unavailable — retrieval must never hard-fail on the reranker.
    /// </summary>
    public virtual async Task<(IReadOnlyList<RetrievalHit> Hits, bool Reranked)> RerankAsync(
        string query, IReadOnlyList<RetrievalHit> candidates, int topK, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return (candidates, false);
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                query,
                documents = candidates.Select(c => new { text = c.Text }).ToArray(),
            });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("rerank", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (ParseAndReorder(body, candidates, topK), true);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            // Reranker down/timeout: degrade to GF order, flag it.
            return (candidates.Take(topK).ToArray(), false);
        }
    }

    internal static IReadOnlyList<RetrievalHit> ParseAndReorder(
        string json, IReadOnlyList<RetrievalHit> candidates, int topK)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");
        var output = new List<RetrievalHit>();
        foreach (var r in results.EnumerateArray())
        {
            var idx = r.GetProperty("index").GetInt32();
            if (idx < 0 || idx >= candidates.Count) continue;
            var score = r.TryGetProperty("relevance_score", out var s) ? s.GetDouble() : 0;
            output.Add(candidates[idx] with { Score = score });
            if (output.Count >= topK) break;
        }
        return output;
    }
}
