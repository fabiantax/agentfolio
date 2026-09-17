namespace AgentFolio.UnitTests;

// global:: alias: the Saves slice still declares namespace AgentFolio.Slices.Saves,
// which hijacks plain `using Slices.Stars` resolution inside AgentFolio.UnitTests
// (C# namespace-lookup shadowing — same class as the PR #50 namespace unification).
using StarsClient = global::Slices.Stars.HttpGitHubStarsClient;
using Xunit;

/// <summary>
/// ParseStarredPage contract, pinned against the LIVE GitHub response shape
/// (recorded 2026-09-17 against api.github.com, Accept: application/vnd.github.star+json,
/// account fabiantax): items are { "starred_at": ..., "repo": {...} } and "topics"
/// is a plain string array. The pre-fix code expected the DOCUMENTED shape
/// ("repository" wrapper + GraphQL nodes topics) and KeyNotFound'd on the live one.
/// </summary>
public sealed class StarsHttpParseTests
{
    private const string LiveShapePage = """
        [
          {
            "starred_at": "2026-08-20T10:00:00Z",
            "repo": {
              "id": 79908301,
              "name": "tree-sitter",
              "full_name": "tree-sitter/tree-sitter",
              "description": "An incremental parsing system for programming tools",
              "html_url": "https://github.com/tree-sitter/tree-sitter",
              "stargazers_count": 20000,
              "language": "C",
              "topics": ["parsing", "incremental-parsing", "rust"],
              "owner": { "login": "tree-sitter" }
            }
          },
          {
            "starred_at": "2026-09-01T12:30:00Z",
            "repo": {
              "id": 123456789,
              "name": "graphfusion",
              "full_name": "fabiantax/graphfusion",
              "description": null,
              "html_url": "https://github.com/fabiantax/graphfusion",
              "stargazers_count": 42,
              "language": "Rust",
              "topics": [],
              "owner": { "login": "fabiantax" }
            }
          }
        ]
        """;

    [Fact]
    public void ParsesLiveRepoWrapperAndStringArrayTopics()
    {
        var page = StarsClient.ParseStarredPage(LiveShapePage);

        Assert.Equal(2, page.Count);
        var first = page[0];
        Assert.Equal(79908301, first.RepoId);
        Assert.Equal("tree-sitter", first.Name);
        Assert.Equal("https://github.com/tree-sitter/tree-sitter", first.HtmlUrl);
        Assert.Equal(20000, first.Stars);
        Assert.Equal("C", first.Language);
        Assert.Equal("tree-sitter", first.Owner);
        Assert.Equal("2026-08-20T10:00:00Z", first.StarredAtIso);
        Assert.Equal(new[] { "parsing", "incremental-parsing", "rust" }, first.Topics.ToArray());
        // null description must surface as null, not empty or thrown.
        Assert.Null(page[1].Description);
        Assert.Empty(page[1].Topics);
    }

    [Fact]
    public void ParsesDocumentedRepositoryWrapperAndNodesTopics()
    {
        const string documented = """
            [
              {
                "starred_at": "2026-01-02T03:04:05Z",
                "repository": {
                  "id": 1,
                  "name": "docs-variant",
                  "full_name": "o/docs-variant",
                  "description": "d",
                  "html_url": "https://github.com/o/docs-variant",
                  "stargazers_count": 7,
                  "language": "Go",
                  "topics": { "nodes": ["a", "b"] },
                  "owner": { "login": "o" }
                }
              }
            ]
            """;

        var page = StarsClient.ParseStarredPage(documented);

        var item = Assert.Single(page);
        Assert.Equal("docs-variant", item.Name);
        Assert.Equal(new[] { "a", "b" }, item.Topics.ToArray());
    }
}
