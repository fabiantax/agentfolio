using AgentFolio.Contracts;
using Slices.Skills;

namespace AgentFolio.UnitTests;

public class SkillsTaxonomyTests
{
    private static StarredRepoRecord Star(long id, string fullName, string? desc, string? topics) =>
        new(id, fullName, desc, topics, DateTimeOffset.UtcNow);

    private static SavedPostRecord Save(string url, string? title) =>
        new(url, title, null, IsStale: false, DateTimeOffset.UtcNow);

    private static IReadOnlyList<StarredRepoRecord> SyntheticStars() => new[]
    {
        Star(1, "acme/rust-cli", null, """["rust","cli"]"""),
        Star(2, "acme/rust-core", "A library core", """["rust"]"""),
        Star(3, "acme/csharp-ai", null, """["c#","ai"]"""),
    };

    private static IReadOnlyList<SavedPostRecord> SyntheticSaves() => new[]
    {
        Save("https://www.linkedin.com/posts/1", "Rust tooling is eating the world"),
        Save("https://www.linkedin.com/posts/2", "Great tooling tips for systems developers"),
    };

    [Fact]
    public void Rust_Ranks_First_With_2_Stars_And_1_Save()
    {
        var taxonomy = SkillTaxonomy.Derive(SyntheticStars(), SyntheticSaves());

        var rust = Assert.Single(taxonomy, s => s.Skill == "rust");
        Assert.Equal(2, rust.StarCount);
        Assert.Equal(1, rust.SaveCount);
        Assert.Equal(3, rust.StarCount + rust.SaveCount);
        Assert.Equal("rust", taxonomy[0].Skill);
    }

    [Fact]
    public void CSharp_Is_Present()
    {
        var taxonomy = SkillTaxonomy.Derive(SyntheticStars(), SyntheticSaves());
        Assert.Contains(taxonomy, s => s.Skill == "c#");
    }

    [Fact]
    public void Evidence_Urls_Are_Correct()
    {
        var taxonomy = SkillTaxonomy.Derive(SyntheticStars(), SyntheticSaves());

        var rust = Assert.Single(taxonomy, s => s.Skill == "rust");
        Assert.Equal(
            new[]
            {
                "https://github.com/acme/rust-cli",
                "https://github.com/acme/rust-core",
                "https://www.linkedin.com/posts/1",
            },
            rust.EvidenceUrls);

        var tooling = Assert.Single(taxonomy, s => s.Skill == "tooling");
        Assert.Equal(
            new[] { "https://www.linkedin.com/posts/1", "https://www.linkedin.com/posts/2" },
            tooling.EvidenceUrls);
    }

    [Fact]
    public void Stopwords_And_Short_Words_Are_Filtered()
    {
        var taxonomy = SkillTaxonomy.Derive(
            Array.Empty<StarredRepoRecord>(),
            new[] { Save("https://www.linkedin.com/posts/3", "The with this that Tools of DevOps and Go") });

        // "the","with","this","that","tools" filtered (stopword or <4 chars or exact "tools");
        var skills = taxonomy.Select(t => t.Skill).ToList();
        Assert.Contains("devops", skills);
        Assert.DoesNotContain("with", skills);
        Assert.DoesNotContain("this", skills);
        Assert.DoesNotContain("tools", skills);
        Assert.DoesNotContain("go", skills); // too short
    }

    [Fact]
    public void Malformed_TopicsJson_Yields_No_Topics_And_Does_Not_Throw()
    {
        var stars = new[]
        {
            Star(9, "bad/json", null, "{not valid json,,"),
            Star(10, "empty/null", null, null),
            Star(11, "wrong-kind", null, "\"just-a-string\""),
            Star(11, "non-strings", null, "[1,2,true]"),
        };

        var taxonomy = SkillTaxonomy.Derive(stars, Array.Empty<SavedPostRecord>());
        Assert.Empty(taxonomy); // no topics, no exact-language descriptions
    }

    [Fact]
    public void Exact_Language_Description_Is_A_Skill()
    {
        var stars = new[] { Star(42, "acme/zig-thing", "zig", null) };
        var taxonomy = SkillTaxonomy.Derive(stars, Array.Empty<SavedPostRecord>());
        Assert.Contains(taxonomy, s => s.Skill == "zig");
    }

    [Fact]
    public async Task GetSkillsAsync_Materializes_And_Derives()
    {
        var store = new FakeStore(SyntheticStars(), SyntheticSaves());
        var taxonomy = await SkillsQuery.GetSkillsAsync(store, CancellationToken.None);

        Assert.Equal("rust", taxonomy[0].Skill);
        Assert.Contains(taxonomy, s => s.Skill == "ai");
    }

    private sealed class FakeStore(
        IReadOnlyList<StarredRepoRecord> stars,
        IReadOnlyList<SavedPostRecord> saves) : IItemStore
    {
        public Task UpsertStarAsync(StarredRepoRecord repo, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertSaveAsync(SavedPostRecord post, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<StarredRepoRecord> QueryStarsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var s in stars) { await Task.Yield(); yield return s; }
        }

        public async IAsyncEnumerable<SavedPostRecord> QuerySavesAsync(bool includeStale = false, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var s in saves) { await Task.Yield(); yield return s; }
        }
    }
}
