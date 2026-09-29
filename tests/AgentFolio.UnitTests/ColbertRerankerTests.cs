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

    [Fact]
    public async Task Rerank_sends_enriched_rerank_text_when_present()
    {
        // A/B winner 2026-09-29 (9/10 vs 7/10): the reranker must score RerankText
        // (desc + use_for + problems) when the hit carries one. Captured via a local
        // stub endpoint echoing the recorded body; assert the outgoing payload.
        string? sent = null;
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18092/");
        listener.Start();
        var serverTask = System.Threading.Tasks.Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
            sent = await reader.ReadToEndAsync();
            var body = System.Text.Encoding.UTF8.GetBytes(Body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });

        var candidates = new[]
        {
            new RetrievalHit("a", "Repo", "short desc", 1.0, 1, "t/repo")
                { RerankText = "short desc. Used for CI pipelines. Solves flaky builds." },
            new RetrievalHit("b", "Repo", "plain hit no enrichment", 0.9, 2, "t/plain"),
        };
        var reranker = new ColbertReranker("http://127.0.0.1:18092");
        var (hits, reranked) = await reranker.RerankAsync("q", candidates, topK: 2);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(reranked);
        Assert.NotNull(sent);
        Assert.Contains("Solves flaky builds.", sent);           // enriched text sent
        Assert.Contains("plain hit no enrichment", sent);        // fallback for plain hits
        Assert.DoesNotContain("\"short desc\"", sent.Replace("short desc. Used", "X")); // not raw Text for hit a
        Assert.Equal("t/repo", hits[0].RepoFullName);            // recorded body order preserved
    }
}
