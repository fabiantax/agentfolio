using Slices.Stars.Retrieval;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#66 stage C2: ColBERT rerank — recorded-shape parsing, degrade-open fallback, topK cut.</summary>
public class ColbertRerankerTests
{
    private const string Body = """
    {"results":[{"index":2,"relevance_score":19.16},{"index":0,"relevance_score":12.0},{"index":1,"relevance_score":3.2}],"model":"jinaai/jina-colbert-v2"}
    """;

    private static System.Collections.Generic.IReadOnlyList<RetrievalHit> Candidates() => new[]
    {
        new RetrievalHit("a", "Repo", "arch test lib", 1.0, 1, "TNG/ArchUnitNET"),
        new RetrievalHit("b", "Repo", "browser harness", 0.9, 2, "browser-use/harness"),
        new RetrievalHit("c", "Repo", "graph cartography", 0.8, 3, "cartography-cncf/cartography"),
    };

    [Fact]
    public void Parse_reorders_by_colbert_score_and_cuts_topk()
    {
        var hits = ColbertReranker.ParseAndReorder(Body, Candidates(), topK: 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal("cartography-cncf/cartography", hits[0].RepoFullName); // index 2 first
        Assert.Equal(19.16, hits[0].Score);
        Assert.Equal("TNG/ArchUnitNET", hits[1].RepoFullName);
    }

    [Fact]
    public void Out_of_range_indices_are_skipped()
    {
        const string bad = """{"results":[{"index":9,"relevance_score":1.0},{"index":1,"relevance_score":2.0}]}""";
        var hits = ColbertReranker.ParseAndReorder(bad, Candidates(), topK: 5);
        var hit = Assert.Single(hits);
        Assert.Equal("browser-use/harness", hit.RepoFullName);
    }

    [Fact]
    public async Task Unavailable_reranker_degrades_to_input_order_flagged()
    {
        var reranker = new ColbertReranker("http://127.0.0.1:1"); // nothing listens here
        var (hits, reranked) = await reranker.RerankAsync("q", Candidates(), topK: 3);
        Assert.False(reranked);
        Assert.Equal(3, hits.Count); // GF order preserved
    }

    [Fact]
    public async Task Empty_candidates_short_circuit()
    {
        var reranker = new ColbertReranker("http://127.0.0.1:1");
        var (hits, reranked) = await reranker.RerankAsync("q", Array.Empty<RetrievalHit>(), topK: 3);
        Assert.False(reranked);
        Assert.Empty(hits);
    }
}
