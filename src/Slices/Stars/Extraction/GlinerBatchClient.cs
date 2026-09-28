namespace Slices.Stars.Extraction;

/// <summary>
/// GLiNER 2.5 batch adapter (:8095). Contract MEASURED live 2026-09-28:
///   POST /extract_batch  {"texts": [...], "entity_labels": [...], "threshold": 0.3}
///   -> {"results":[{"entities":{"Label":[{"text","confidence","start","end"}]},"relations":{}}], "elapsed_ms"}
/// THE FIELD IS entity_labels — "entity_types" is rejected 422 (measured).
/// Response per-entity uses "confidence" (NOT "score"), and entities are a map label->mentions.
/// </summary>
public sealed class GlinerBatchClient : IEntityExtractor
{
    private readonly HttpClient _http;
    private readonly int _batchSize;

    public GlinerBatchClient(string baseUrl, int batchSize = 8, int timeoutSeconds = 120)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _batchSize = batchSize;
    }

    public async Task<IReadOnlyList<ExtractionResult>> ExtractAsync(
        IReadOnlyList<string> texts, IReadOnlyList<string> entityLabels, CancellationToken ct = default)
    {
        var all = new List<ExtractionResult>(texts.Count);
        for (var i = 0; i < texts.Count; i += _batchSize)
        {
            var slice = texts.Skip(i).Take(_batchSize).ToArray();
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                texts = slice,
                entity_labels = entityLabels,
                threshold = 0.3
            });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("extract_batch", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            all.AddRange(ParseBatch(body, slice.Length));
        }
        return all;
    }

    /// <summary>Parse the recorded :8095 batch shape (also accepts veld-contract "score" naming).</summary>
    public static IReadOnlyList<ExtractionResult> ParseBatch(string json, int expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");
        var list = new List<ExtractionResult>(expected);
        foreach (var r in results.EnumerateArray())
        {
            var entities = new List<ExtractedEntity>();
            if (r.TryGetProperty("entities", out var ents) && ents.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var labelGroup in ents.EnumerateObject())
                {
                    if (labelGroup.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    foreach (var m in labelGroup.Value.EnumerateArray())
                    {
                        double score =
                            m.TryGetProperty("confidence", out var c) ? c.GetDouble() :
                            m.TryGetProperty("score", out var s) ? s.GetDouble() : 0;
                        entities.Add(new ExtractedEntity(
                            m.GetProperty("text").GetString() ?? "",
                            labelGroup.Name,
                            m.TryGetProperty("start", out var st) ? st.GetInt32() : 0,
                            m.TryGetProperty("end", out var en) ? en.GetInt32() : 0,
                            score));
                    }
                }
            }
            var relations = new List<ExtractedRelation>();
            if (r.TryGetProperty("relations", out var rels))
            {
                if (rels.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var rel in rels.EnumerateArray())
                    {
                        relations.Add(new ExtractedRelation(
                            rel.TryGetProperty("head_text", out var ht) ? ht.GetString() ?? "" : "",
                            rel.TryGetProperty("head_label", out var hl) ? hl.GetString() ?? "" : "",
                            rel.TryGetProperty("tail_text", out var tt) ? tt.GetString() ?? "" : "",
                            rel.TryGetProperty("tail_label", out var tl) ? tl.GetString() ?? "" : "",
                            rel.TryGetProperty("relation", out var rn) ? rn.GetString() ?? "" : "",
                            rel.TryGetProperty("score", out var rs) ? rs.GetDouble() : 0));
                    }
                }
                else if (rels.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // veld contract: relations keyed by label -> [ {head, tail, score} ]
                    foreach (var relGroup in rels.EnumerateObject())
                    {
                        if (relGroup.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                        foreach (var rel in relGroup.Value.EnumerateArray())
                        {
                            relations.Add(new ExtractedRelation(
                                rel.TryGetProperty("head", out var h) ? h.GetString() ?? "" : "",
                                "", // veld bundle does not type head/tail mentions
                                rel.TryGetProperty("tail", out var t) ? t.GetString() ?? "" : "",
                                "",
                                relGroup.Name,
                                rel.TryGetProperty("score", out var rs) ? rs.GetDouble() : 0));
                        }
                    }
                }
            }
            list.Add(new ExtractionResult(entities, relations));
        }
        return list;
    }
}
