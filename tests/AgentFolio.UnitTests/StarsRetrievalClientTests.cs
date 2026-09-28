using Slices.Stars.Retrieval;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#66 stage C1: retrieval client — GF-first contract, response parsing, citation fields.</summary>
public class StarsRetrievalClientTests
{
    private sealed class FakeGf(System.Collections.Generic.Dictionary<string, string> replies) : StarsRetrievalClient("http://127.0.0.1:1")
    {
        public System.Collections.Generic.List<string> Sent { get; } = new();
        public override System.Threading.Tasks.Task<string> SendAsync(string message, System.Threading.CancellationToken ct = default)
        {
            Sent.Add(message);
            foreach (var (key, value) in replies)
                if (message.Contains(key)) return System.Threading.Tasks.Task.FromResult(value);
            return System.Threading.Tasks.Task.FromResult(replies["*"]);
        }
    }

    private const string RowsJson = """
    {"rows":[
      {"kind":"Chunk","id":"7:0","text":"GraphFusion is a hybrid graph engine","score":0.93,"repoId":7,"fullName":"knowledgator/GraphFusion"}
    ]}
    """;

    [Fact]
    public async Task MultiHop_builds_traversal_cypher_with_graph_prefix()
    {
        var gf = new FakeGf(new() { ["*"] = RowsJson });
        var hits = await gf.SearchAsync(RetrievalStrategy.MultiHop, "graph engines");
        var hit = Assert.Single(hits);
        Assert.Equal("Chunk", hit.NodeKind);
        Assert.Equal(7, hit.RepoId);
        Assert.Equal("knowledgator/GraphFusion", hit.RepoFullName); // citation contract
        Assert.Contains("USE GRAPH agentfolio_stars", gf.Sent[0]);
        Assert.Contains(gf.Sent, m => m.Contains("mentions_repo")); // 2nd-hop traversal leg
    }

    [Fact]
    public async Task Bm25_sends_gf_side_scoring_query()
    {
        var gf = new FakeGf(new() { ["*"] = RowsJson });
        await gf.SearchAsync(RetrievalStrategy.Bm25, "rust agents");
        Assert.Contains("USE GRAPH agentfolio_stars", gf.Sent[0]);
        Assert.Contains("rust agents", gf.Sent[0]);
    }

    [Fact]
    public async Task Vector_strategy_targets_gf_embed_and_knn()
    {
        var gf = new FakeGf(new() { ["*"] = RowsJson });
        await gf.SearchAsync(RetrievalStrategy.Vector, "knowledge graph");
        Assert.Contains("vector_knn_nodes", gf.Sent[0]);
        Assert.Contains("gf.embed", gf.Sent[0]);
    }

    [Fact]
    public void Parse_tolerates_non_json_output()
    {
        var hits = StarsRetrievalClient.ParseRows("<html>Gateway timeout</html>");
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Escapes_single_quotes_in_user_query()
    {
        var gf = new FakeGf(new() { ["*"] = RowsJson });
        await gf.SearchAsync(RetrievalStrategy.Bm25, "it's O'Brien's repo");
        Assert.DoesNotContain("it's O'Brien's", gf.Sent[0]); // raw unescaped must not appear
        Assert.Contains("it\\'s O\\'Brien\\'s", gf.Sent[0]);
    }
}
