using System.Net;
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


    // ---------- #87: a2a wire envelope + Repo keyword recall ----------

    [Fact]
    public void ExtractKeywords_StripsStopwordsAndShortTerms()
    {
        var kws = StarsRetrievalClient.ExtractKeywords(
            "Which starred repos help run end-to-end browser tests?");
        Assert.DoesNotContain("which", kws);
        Assert.DoesNotContain("help", kws);
        Assert.DoesNotContain("run", kws);
        Assert.Contains("browser", kws);
        Assert.Contains("end-to-end", kws);
        Assert.Contains("tests", kws);
        Assert.Equal(kws.Count, kws.Distinct().Count());
    }

    [Fact]
    public void ExtractArtifactText_ParsesA2aTaskResponse()
    {
        const string body = """
            {"jsonrpc":"2.0","result":{"task":{"artifacts":[{"id":"a1","parts":[{"text":"[{\"id\":\"a/b\"}]","type":"text"}]}],"status":{"state":"completed"}}},"id":"p1"}
            """;
        Assert.Equal("""[{"id":"a/b"}]""", StarsRetrievalClient.ExtractArtifactText(body));
    }

    private sealed class WireHandler(Action<string> onBody, string reply) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onBody(request.Content is not null
                ? request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()
                : "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static StarsRetrievalClient WireClient(out System.Collections.Generic.List<string> bodies, string reply)
    {
        bodies = new System.Collections.Generic.List<string>();
        var client = new StarsRetrievalClient("http://gf.test");
        var field = typeof(StarsRetrievalClient).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(client, new HttpClient(new WireHandler(bodies.Add, reply)) { BaseAddress = new Uri("http://gf.test") });
        return client;
    }

    private const string EnvelopeReply = """
        {"jsonrpc":"2.0","result":{"task":{"artifacts":[{"parts":[{"text":"[{\"id\":\"x/y\",\"kind\":\"Repo\",\"text\":\"t\",\"score\":\"1.0\",\"repoId\":\"\",\"fullName\":\"x/y\"}]","type":"text"}]}],"status":{"state":"completed"}}},"id":"1"}
        """;

    [Fact]
    public async Task SendAsync_WrapsCypherInA2aEnvelope_AndParsesBareArrayRows()
    {
        // #87 wire capture: bare text/plain POST 200s EMPTY on the live gf-a2a —
        // the JSON-RPC send_message envelope is the contract; artifact text is a
        // bare JSON array (no "rows" wrapper), repoId projects as "" not a number.
        var client = WireClient(out var bodies, EnvelopeReply);
        var rows = await client.SearchAsync(RetrievalStrategy.Repo, "browser tests", 3);

        Assert.Single(bodies);
        Assert.Contains("\"send_message\"", bodies[0]);
        Assert.Contains("USE GRAPH", bodies[0]); // cypher rode inside the envelope
        Assert.Single(rows); // single row in the pinned artifact reply
        Assert.Equal("x/y", rows[0].RepoFullName);
        Assert.Null(rows[0].RepoId);
    }

    [Fact]
    public async Task SearchAsync_RepoStrategy_BuildsKeywordOrCypher()
    {
        var client = WireClient(out var bodies, EnvelopeReply);
        await client.SearchAsync(RetrievalStrategy.Repo, "which browser tools automate e2e flows", 5);
        Assert.Contains("'browser'", bodies[0]);
        Assert.Contains("'automate'", bodies[0]);
        Assert.Contains("ORDER BY score DESC", bodies[0]);
        Assert.DoesNotContain("'which'", bodies[0]);
    }
}
