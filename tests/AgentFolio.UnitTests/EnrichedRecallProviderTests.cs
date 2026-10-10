using AgentFolio.Contracts;

namespace Slices.Stars.Retrieval;

/// <summary>
/// #123 (preview-gate): the A/B-winner enriched-recall arm, RED-first. The
/// harness tokenizer is copied VERBATIM (whitespace split, len&gt;3, no stopword
/// removal, no dedupe) and 'ci/cd' must stay ONE term.
/// </summary>
public class EnrichedRecallProviderTests
{
    private static EnrichedRepo Repo(
        long id, string fullName, string? desc = null,
        string? topics = null, string? useFor = null, string? problems = null)
        => new(id, fullName, desc, topics, useFor, problems);

    [Fact]
    public async Task Recall_ranks_enriched_doc_matches_best_first()
    {
        var provider = new EnrichedRecallProvider(_ => Task.FromResult<IReadOnlyList<EnrichedRepo>>(new[]
        {
            Repo(2, "woodpecker-ci/woodpecker",
                desc: "Woodpecker is a simple, yet powerful CI/CD engine with great extensibility.",
                useFor: "self-hosted CI/CD pipelines"),
            Repo(1, "unrelated/thing", desc: "A recipe manager for home cooks."),
        }));

        var hits = await provider.RecallAsync("tool for CI/CD pipelines that is self-hosted", topN: 5);

        Assert.NotEmpty(hits);
        Assert.Equal("woodpecker-ci/woodpecker", hits[0].RepoFullName);
        Assert.True(hits[0].Score >= 2); // 'ci/cd' + 'self-hosted' both hit the hay
    }

    [Fact]
    public async Task Recall_hits_carry_reranktext_with_enrichment_labels()
    {
        var provider = new EnrichedRecallProvider(_ => Task.FromResult<IReadOnlyList<EnrichedRepo>>(new[]
        {
            Repo(7, "acme/widget",
                desc: "A widget engine.",
                useFor: "building widgets",
                problems: "[\"no good widgets\"]"),
        }));

        var hits = await provider.RecallAsync("widget engine", topN: 5);

        var hit = Assert.Single(hits);
        Assert.Equal("A widget engine.", hit.Text); // citation snippet stays the description
        Assert.NotNull(hit.RerankText);
        Assert.Contains("use for:", hit.RerankText);
        Assert.Contains("problems:", hit.RerankText);
        Assert.Contains("acme/widget", hit.RerankText);
    }

    [Fact]
    public async Task Recall_returns_empty_for_no_keyword_matches()
    {
        var provider = new EnrichedRecallProvider(_ => Task.FromResult<IReadOnlyList<EnrichedRepo>>(new[]
        {
            Repo(1, "a/thing", desc: "nothing here"),
        }));

        var hits = await provider.RecallAsync("zzzqqq unrelatedterm", topN: 5);

        Assert.Empty(hits); // adapter falls through to GF arms
    }

    [Fact]
    public async Task Recall_degrades_open_when_loader_fails()
    {
        var provider = new EnrichedRecallProvider(
            _ => throw new Microsoft.Data.Sqlite.SqliteException(
                "boom", 1));

        var hits = await provider.RecallAsync("woodpecker ci", topN: 5);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Recall_harness_tokenizer_verbatim()
    {
        // 'ci/cd' must stay ONE term (len>3 with the slash), not split on '/'.
        var kws = EnrichedRecallProvider.HarnessKeywords("tool for CI/CD pipelines that is self-hosted");
        Assert.Contains("ci/cd", kws);
        Assert.DoesNotContain("ci", kws);
        Assert.DoesNotContain("cd", kws);
        // len>3 only — tool(4)/ci/cd(5)/pipelines(9)/that(4)/self-hosted(11) survive; 'for'/'is' don't.
        Assert.Contains("self-hosted", kws);
        Assert.Equal(5, kws.Count);
    }

    [Fact]
    public async Task Recall_topics_json_normalized_into_hay()
    {
        var provider = new EnrichedRecallProvider(_ => Task.FromResult<IReadOnlyList<EnrichedRepo>>(new[]
        {
            Repo(3, "ci/tooling",
                topics: "[\"ci-cd\",\"devops\",\"self-hosted\"]"),
        }));

        var hits = await provider.RecallAsync("devops self-hosted", topN: 5);

        var hit = Assert.Single(hits);
        Assert.True(hit.Score >= 2);
    }
}
