using Slices.Stars.Graph;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#70 stage B4: Cypher builder — idempotent MERGE shapes, escaping, deterministic ids.</summary>
public class StarsGraphCypherTests
{
    [Fact]
    public void Repo_upsert_is_merge_based_and_escapes_quotes()
    {
        var cy = StarsGraphCypher.UpsertRepo(42, "own/re'po", "desc with 'quote'", null);
        Assert.Contains("MERGE (r:Repo {repoId: 42})", cy);
        Assert.Contains("re\\'po", cy);
        Assert.Contains("null", cy); // missing language -> null literal
    }

    [Fact]
    public void Chunk_upsert_links_to_repo_and_uses_deterministic_id()
    {
        var cy = StarsGraphCypher.UpsertChunk(7, 3, "chunk text", "A > B");
        Assert.Contains("chunkId: '7:3'", cy);
        Assert.Contains("MERGE (r)-[:has_chunk]->(c)", cy);
    }

    [Fact]
    public void Next_link_targets_adjacent_chunks()
    {
        var cy = StarsGraphCypher.LinkNextChunk(9, 4);
        Assert.Contains("'9:3'", cy);
        Assert.Contains("'9:4'", cy);
        Assert.Contains("MERGE (a)-[:next]->(b)", cy);
    }

    [Fact]
    public void Mention_edge_is_between_repos()
    {
        var cy = StarsGraphCypher.LinkMention(1, 2);
        Assert.Contains("(:Repo {repoId: 1})", cy.Replace("(a:Repo {repoId: 1})", "(:Repo {repoId: 1})"));
        Assert.Contains("MERGE (a)-[:mentions_repo]->(b)", cy);
    }
}
