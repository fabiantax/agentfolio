namespace Slices.Stars.Extraction;

/// <summary>
/// GLiNER-Relex adapter (:8090) — the PROSE relation default (#66 B3):
/// doc-level RE 2.4x GLiFormer joint head (measured card table, #66 c76450);
/// live probe got both relations right in 0.51s on an agentfolio sentence.
/// Contract measured live: POST /extract {text, entity_labels[], relation_labels[], threshold}
/// -> {"entities":[{text,label,start,end,score}], "relations":[{head_text,head_label,tail_text,tail_label,relation,score}]}
/// </summary>
public sealed class RelexClient : IRelationExtractor
{
    private readonly HttpClient _http;

    public RelexClient(string baseUrl, int timeoutSeconds = 120)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    public async Task<IReadOnlyList<ExtractionResult>> ExtractRelationsAsync(
        IReadOnlyList<string> texts, IReadOnlyList<string> entityLabels,
        IReadOnlyList<string> relationLabels, CancellationToken ct = default)
    {
        var all = new List<ExtractionResult>(texts.Count);
        foreach (var text in texts) // relex service is single-text (measured); pacing is the caller's job
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                text,
                entity_labels = entityLabels,
                relation_labels = relationLabels,
                threshold = 0.3
            });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("extract", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            all.Add(ParseSingle(body));
        }
        return all;
    }

    public static ExtractionResult ParseSingle(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var entities = new List<ExtractedEntity>();
        if (doc.RootElement.TryGetProperty("entities", out var ents) && ents.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var e in ents.EnumerateArray())
            {
                entities.Add(new ExtractedEntity(
                    e.GetProperty("text").GetString() ?? "",
                    e.GetProperty("label").GetString() ?? "",
                    e.TryGetProperty("start", out var st) ? st.GetInt32() : 0,
                    e.TryGetProperty("end", out var en) ? en.GetInt32() : 0,
                    e.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0));
            }
        }
        var relations = new List<ExtractedRelation>();
        if (doc.RootElement.TryGetProperty("relations", out var rels) && rels.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var r in rels.EnumerateArray())
            {
                relations.Add(new ExtractedRelation(
                    r.TryGetProperty("head_text", out var ht) ? ht.GetString() ?? "" : "",
                    r.TryGetProperty("head_label", out var hl) ? hl.GetString() ?? "" : "",
                    r.TryGetProperty("tail_text", out var tt) ? tt.GetString() ?? "" : "",
                    r.TryGetProperty("tail_label", out var tl) ? tl.GetString() ?? "" : "",
                    r.TryGetProperty("relation", out var rn) ? rn.GetString() ?? "" : "",
                    r.TryGetProperty("score", out var rs) ? rs.GetDouble() : 0));
            }
        }
        return new ExtractionResult(entities, relations);
    }
}
