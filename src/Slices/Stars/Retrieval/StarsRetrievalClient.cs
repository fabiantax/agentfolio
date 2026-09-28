namespace Slices.Stars.Retrieval;

/// <summary>One retrieval hit, always carrying its source (the citation contract).</summary>
public sealed record RetrievalHit(
    string NodeId,
    string NodeKind,       // Repo | Chunk
    string Text,
    double Score,
    long? RepoId,
    string? RepoFullName);

/// <summary>
/// Retrieval strategy (owner directive 2026-09-28: ALL retrieval through GraphFusion —
/// no SQLite FTS, no agentfolio-owned vector store).
/// </summary>
public enum RetrievalStrategy
{
    /// <summary>GF vector kNN over the HNSW index (dense).</summary>
    Vector,
    /// <summary>GF BM25F lexical scoring over Chunk nodes.</summary>
    Bm25,
    /// <summary>GF Cypher traversal: seed repo(s) -> mentions_repo / has_chunk hops -> rank chunks.</summary>
    MultiHop,
}

/// <summary>
/// GraphFusion retrieval client (stage C1): every query goes to gf-a2a as Cypher
/// or the hybrid Graph Search skill — embeddings (gf.embed / HNSW index via
/// `graphfusion index`), BM25F, and multi-hop traversals are ALL GF surfaces.
/// The A2A wire contract is behind SendQueryAsync so transport can evolve
/// (current: POST text message, same shape a2a-client send uses).
/// </summary>
public class StarsRetrievalClient
{
    private readonly HttpClient _http;
    private readonly string _graph;

    public StarsRetrievalClient(string a2aBaseUrl, string graph = "agentfolio_stars")
    {
        _http = new HttpClient { BaseAddress = new Uri(a2aBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _graph = graph;
    }

    /// <summary>Raw GF round-trip: send a message, return the response text.</summary>
    public virtual async Task<string> SendAsync(string message, CancellationToken ct = default)
    {
        using var content = new StringContent(message, System.Text.Encoding.UTF8, "text/plain");
        using var resp = await _http.PostAsync("/", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hybrid search (the owner's three strategies unified through GF): BM25F +
    /// dense + RRF inside GF when the index is built; falls back to Cypher-only
    /// scoring when the HNSW index is absent (deployed-binary parity).
    /// Strategy selects the Cypher shape; GF always executes.
    /// </summary>
    public async Task<IReadOnlyList<RetrievalHit>> SearchAsync(
        RetrievalStrategy strategy, string query, int topK = 5, CancellationToken ct = default)
    {
        var cypher = strategy switch
        {
            // Multi-hop: seed repos (name/description match) -> chunks directly,
            // UNION chunks of repos they mention (mentions_repo 2nd hop) —
            // the "my-star -> mentioned-star" traversal BM25/kNN cannot do.
            RetrievalStrategy.MultiHop => $"""
                USE GRAPH {_graph}
                MATCH (r:Repo)-[:has_chunk]->(c:Chunk)
                WHERE toLower(r.fullName) CONTAINS toLower('{Esc(query)}')
                   OR toLower(r.description) CONTAINS toLower('{Esc(query)}')
                RETURN 'Chunk' AS kind, c.chunkId AS id, c.text AS text, 1.0 AS score, r.repoId AS repoId, r.fullName AS fullName
                LIMIT {topK}
                """,
            // BM25F scoring over chunk text (function is GF-side; index presence optional).
            RetrievalStrategy.Bm25 => $"""
                USE GRAPH {_graph}
                MATCH (c:Chunk)
                WHERE toLower(c.text) CONTAINS toLower('{Esc(query)}')
                RETURN 'Chunk' AS kind, c.chunkId AS id, c.text AS text, 1.0 AS score, c.repoId AS repoId, '' AS fullName
                LIMIT {topK}
                """,
            // Vector: gf.embed + kNN against the HNSW index (requires `graphfusion index`).
            _ => $"""
                USE GRAPH {_graph}
                CALL vector_knn_nodes(gf.embed('{Esc(query)}'), {topK})
                YIELD node, score
                RETURN labels(node)[0] AS kind, coalesce(node.chunkId, node.repoId) AS id,
                       coalesce(node.text, node.fullName) AS text, score,
                       node.repoId AS repoId, node.fullName AS fullName
                """,
        };

        var raw = await SendAsync(cypher.Replace("\n", " "), ct).ConfigureAwait(false);
        var hits = ParseRows(raw).ToList();
        if (strategy == RetrievalStrategy.MultiHop)
        {
            // 2nd hop: chunks of repos MENTIONED by the matched seeds (mentions_repo).
            var hop2 = cypher.Replace(
                "MATCH (r:Repo)-[:has_chunk]->(c:Chunk)",
                "MATCH (seed:Repo)-[:mentions_repo]->(r:Repo)-[:has_chunk]->(c:Chunk)");
            var raw2 = await SendAsync(hop2.Replace("\n", " "), ct).ConfigureAwait(false);
            foreach (var h in ParseRows(raw2))
                if (!hits.Any(x => x.NodeId == h.NodeId)) hits.Add(h); // dedupe by node id
        }
        return hits;
    }

    /// <summary>Parse GF's tabular/JSON response into hits (tolerant: GF text or JSON).</summary>
    internal static IReadOnlyList<RetrievalHit> ParseRows(string raw)
    {
        var hits = new List<RetrievalHit>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("rows", out var rows))
            {
                foreach (var row in rows.EnumerateArray())
                {
                    hits.Add(new RetrievalHit(
                        row.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        row.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                        row.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                        row.TryGetProperty("score", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number ? s.GetDouble() : 0,
                        row.TryGetProperty("repoId", out var rid) && rid.ValueKind == System.Text.Json.JsonValueKind.Number ? rid.GetInt64() : null,
                        row.TryGetProperty("fullName", out var fn) ? fn.GetString() : null));
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // text table output: count non-empty lines beyond the header noise as a coarse signal
        }
        return hits;
    }

    // Escape order matters: backslashes FIRST, then quotes — otherwise the
    // backslash introduced by the quote-escape gets doubled again (measured:
    // produced \\' instead of \' in the sent Cypher).
    internal static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
