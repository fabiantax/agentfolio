using Slices.Stars.Extraction;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#70 stage B2/B3: adapter parsers against the MEASURED service shapes.</summary>
public class ExtractionAdapterTests
{
    [Fact]
    public void Gliner_parse_handles_confidence_naming_and_label_map()
    {
        // Recorded from live :8095 on 2026-09-28 (field = entity_labels; per-entity = confidence).
        const string body = """
        {"results":[
          {"entities":{"Software":[{"text":"AgentFolio","confidence":0.9594,"start":0,"end":10},
                                     {"text":"GraphFusion","confidence":0.9508,"start":62,"end":73}],
                        "Organization":[{"text":"GitHub","confidence":0.9198,"start":17,"end":23}],
                        "Concept":[{"text":"knowledge graph","confidence":0.976,"start":37,"end":52}]},
           "relations":[]}
        ],"elapsed_ms":3211}
        """;
        var results = GlinerBatchClient.ParseBatch(body, 1);
        var r = Assert.Single(results);
        Assert.Equal(4, r.Entities.Count);
        Assert.Contains(r.Entities, e => e.Label == "Software" && e.Text == "AgentFolio" && e.Score > 0.9);
        Assert.Empty(r.Relations);
    }

    [Fact]
    public void Gliner_parse_accepts_veld_contract_relations_object()
    {
        var body = """
        {"results":[{"entities":{"Person":[{"text":"Alice","confidence":0.9,"start":0,"end":5}]},
                     "relations":{"works_at":[{"head":"Alice","tail":"Acme","score":0.8}]}}]}
        """;
        var results = GlinerBatchClient.ParseBatch(body, 1);
        var r = Assert.Single(results);
        var rel = Assert.Single(r.Relations);
        Assert.Equal("works_at", rel.Relation);
        Assert.Equal("Alice", rel.HeadText);
        Assert.Equal("Acme", rel.TailText);
    }

    [Fact]
    public void Relex_parse_handles_measured_shape()
    {
        // Recorded live 2026-09-28: relations carry head_text/head_label/tail_text/tail_label/relation/score.
        const string body = """
        {"entities":[{"text":"AgentFolio","label":"software","start":0,"end":10,"score":0.995},
                     {"text":"GitHub","label":"software","start":17,"end":23,"score":0.98}],
         "relations":[{"head_text":"AgentFolio","head_label":"software","head_idx":0,
                       "tail_text":"GitHub","tail_label":"software","tail_idx":1,
                       "relation":"integrates with","score":0.99}]}
        """;
        var r = RelexClient.ParseSingle(body);
        Assert.Equal(2, r.Entities.Count);
        var rel = Assert.Single(r.Relations);
        Assert.Equal("AgentFolio", rel.HeadText);
        Assert.Equal("GitHub", rel.TailText);
        Assert.Equal("integrates with", rel.Relation);
        Assert.True(rel.Score > 0.9);
    }
}
