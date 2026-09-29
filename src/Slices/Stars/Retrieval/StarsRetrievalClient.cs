namespace Slices.Stars.Retrieval;

/// <summary>One retrieval hit, always carrying its source (the citation contract).</summary>
public sealed record RetrievalHit(
    string NodeId,
    string NodeKind,       // Repo | Chunk
    string Text,
    double Score,
    long? RepoId,
    string? RepoFullName)
{
    /// <summary>
    /// Optional richer document text for reranking (stage-C A/B winner, 2026-09-29:
    /// desc + use_for + problems reranked 9/10 vs 7/10 desc-only). When set, the
    /// reranker scores this instead of Text; Text stays the citation snippet.
    /// </summary>
    public string? RerankText { get; init; }
}

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
    /// <summary>
    /// Keyword recall over Repo nodes (fullName/description OR-match) — #87: chunk
    /// nodes are absent until the GF ingest sidecar lands (a2a write gate), and the
    /// A/B harness's arm A (7/10 alone) ran exactly this leg.
    /// </summary>
    Repo,
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

    /// <summary>
    /// Stage-C2 pipeline: GF hybrid recall (topN candidates) -> ColBERT late-interaction
    /// rerank (precision) -> topK hits. Reranker degradation falls back to GF order.
    /// </summary>
    public async Task<(IReadOnlyList<RetrievalHit> Hits, bool Reranked)> SearchWithRerankAsync(
        RetrievalStrategy strategy, string query, ColbertReranker reranker,
        int topK = 5, int recallN = 30, CancellationToken ct = default)
    {
        var candidates = await SearchAsync(strategy, query, recallN, ct).ConfigureAwait(false);
        return await reranker.RerankAsync(query, candidates, topK, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Raw GF round-trip: wrap the query in the a2a JSON-RPC envelope
    /// (wire-captured 2026-09-29 #87: {"method":"send_message","params":{"message":
    /// {"parts":[{"text":...,"type":"text"}],"role":"user"}}}) and return the row
    /// text from result.task.artifacts[].parts[].text. The previous bare
    /// text/plain POST returned an empty 200 — every strategy silently got 0 rows.
    /// </summary>
    public virtual async Task<string> SendAsync(string message, CancellationToken ct = default)
    {
        // UnsafeRelaxedJsonEscaping: the default encoder escapes ' as \u0027 —
        // valid JSON but the GF Cypher parser sees \u0027 inside string literals
        // and fails to match repo names (measured #87).
        var envelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "send_message",
            @params = new
            {
                message = new
                {
                    message_id = Guid.NewGuid().ToString("N"),
                    parts = new[] { new { text = message, type = "text" } },
                    role = "user",
                },
            },
            id = Guid.NewGuid().ToString("N"),
        }, new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        using var content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ExtractArtifactText(body);
    }

    /// <summary>Pull artifacts[].parts[].text out of the a2a JSON-RPC task response.</summary>
    internal static string ExtractArtifactText(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("task", out var task) &&
                task.TryGetProperty("artifacts", out var artifacts) &&
                artifacts.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var art in artifacts.EnumerateArray())
                {
                    if (art.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text))
                                sb.Append(text.GetString());
                        }
                    }
                }
                return sb.ToString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through: return the raw body (older/plain responses)
        }
        return body;
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
            // #87 keyword recall over Repo nodes (no Chunk dependency): OR the
            // question's keywords across fullName/description, rank by how many
            // distinct keywords a repo matched. Arm A of the A/B gate (7/10 alone).
            RetrievalStrategy.Repo => BuildRepoRecallCypher(query, topK),
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
        if (string.IsNullOrWhiteSpace(raw)) return hits;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            // GF returns either {"rows":[...]} or a bare JSON array (a2a artifact
            // text is the bare array — wire-captured #87).
            var rows = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("rows", out var r) ? r : default;
            if (rows.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    hits.Add(new RetrievalHit(
                        row.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        row.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                        row.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                        row.TryGetProperty("score", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number ? s.GetDouble() : 0,
                        row.TryGetProperty("repoId", out var rid) && rid.ValueKind == System.Text.Json.JsonValueKind.Number ? rid.GetInt64() : null,
                        row.TryGetProperty("fullName", out var fn) && fn.ValueKind == System.Text.Json.JsonValueKind.String ? fn.GetString() : null));
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
    // produced \' instead of \' in the sent Cypher).
    internal static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>
    /// Question keywords for Repo recall (#87): lowercase, strip stopwords and
    /// short/generic terms, keep order, dedupe. Deliberately simple — recall only;
    /// precision comes from the ColBERT rerank downstream.
    /// </summary>
    internal static IReadOnlyList<string> ExtractKeywords(string query)
    {
        string[] stopwords =
        [
            "the", "a", "an", "and", "or", "of", "to", "for", "in", "on", "with", "is",
            "are", "was", "were", "be", "been", "being", "it", "its", "this", "that",
            "these", "those", "which", "what", "who", "whom", "whose", "how", "why",
            "where", "when", "can", "could", "should", "would", "will", "shall", "may",
            "might", "must", "do", "does", "did", "done", "have", "has", "had", "i",
            "you", "we", "they", "he", "she", "my", "our", "your", "their", "help",
            "helps", "helped", "run", "runs", "running", "use", "used", "using", "uses",
            "get", "gets", "got", "make", "makes", "made", "some", "any", "all", "me",
        ];
        return query
            .Split([' ', ',', ';', ':', '?', '!', '.', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length >= 3 && !stopwords.Contains(t))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Repo-node keyword recall cypher (#87): one MATCH with an OR chain per keyword
    /// across fullName/description; score = count of DISTINCT keywords matched.
    /// Empty keyword set -> impossible predicate, 0 rows (degrade-open upstream).
    /// </summary>
    private string BuildRepoRecallCypher(string query, int topK)
    {
        var kws = ExtractKeywords(query);
        if (kws.Count == 0)
        {
            return $"USE GRAPH {_graph} MATCH (r:Repo) WHERE 1 = 0 RETURN 'Repo' AS kind, '' AS id, '' AS text, 0.0 AS score, r.repoId AS repoId, '' AS fullName LIMIT 0";
        }
        var ors = string.Join("\n           OR ", kws.Select(k =>
            $"toLower(r.fullName) CONTAINS '{Esc(k)}' OR toLower(r.description) CONTAINS '{Esc(k)}'"));
        var scored = string.Join(" +\n               ", kws.Select(k =>
            $"(CASE WHEN toLower(r.fullName) CONTAINS '{Esc(k)}' THEN 1 ELSE 0 END + CASE WHEN toLower(r.description) CONTAINS '{Esc(k)}' THEN 1 ELSE 0 END)"));
        return $"""
            USE GRAPH {_graph}
            MATCH (r:Repo)
            WHERE {ors}
            WITH r, ({scored}) AS kwScore
            RETURN 'Repo' AS kind, r.fullName AS id, r.description AS text, toFloat(kwScore) AS score, r.repoId AS repoId, r.fullName AS fullName
            ORDER BY score DESC
            LIMIT {topK}
            """;
    }
}
