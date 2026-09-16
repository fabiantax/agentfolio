using AgentFolio.Eval;

namespace AgentFolio.UnitTests;

/// <summary>
/// Issue #44 O1: the frozen eval set is well-formed and the frozen scorers
/// behave per eval/README.md. These tests pin the SET (>=20 questions, valid
/// source refs, non-empty gold) and the METRIC FORMULAS (break-it-once guard:
/// if someone redefines a metric, a test goes red).
/// </summary>
public class EvalSetTests
{
    private static string SetPath =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eval", "sets", "v1.json");

    [Fact]
    public void Set_v1_loads_and_meets_frozen_size_contract()
    {
        var set = EvalSet.Load(SetPath);
        Assert.Equal("v1", set.Id);
        Assert.True(set.Questions.Count >= 20, $"eval set has {set.Questions.Count} questions, AC requires >= 20");
        Assert.True(set.Sources.Count >= 4);
        Assert.All(set.Questions, q =>
        {
            Assert.Contains(set.Sources, s => s.Id == q.SourceId);
            Assert.NotEmpty(q.GoldEntities);
            Assert.NotEmpty(q.Question);
        });
        Assert.NotEmpty(set.DistractorEntities);
    }

    [Fact]
    public void EntityScores_full_recall_and_precision_when_only_gold_mentioned()
    {
        var gold = new[] { "Semantic Kernel", "AutoGen" };
        var vocab = gold.Concat(new[] { "LangChain" }).ToList();
        var (p, r, f1) = EvalScorer.EntityScores("It unifies Semantic Kernel and AutoGen.", gold, vocab);
        Assert.Equal(1.0, p);
        Assert.Equal(1.0, r);
        Assert.Equal(1.0, f1);
    }

    [Fact]
    public void EntityScores_punishes_distractor_and_cross_question_entities()
    {
        var gold = new[] { "AutoGen" };
        var vocab = gold.Concat(new[] { "LangChain", "Blazor WebAssembly" }).ToList();
        var (p, r, f1) = EvalScorer.EntityScores("AutoGen with LangChain and Blazor WebAssembly", gold, vocab);
        Assert.Equal(1.0, r);                       // gold found
        Assert.Equal(1.0 / 3, p, precision: 5);      // 1 gold hit of 3 vocab hits
        Assert.True(f1 > 0 && f1 < 1);
    }

    [Fact]
    public void EntityScores_empty_answer_scores_zero()
    {
        var (p, r, f1) = EvalScorer.EntityScores("", new[] { "vec0" }, new[] { "vec0", "Redis" });
        Assert.Equal(0.0, p);
        Assert.Equal(0.0, r);
        Assert.Equal(0.0, f1);
    }

    [Fact]
    public void EntityScores_case_insensitive_and_partial_gold_miss()
    {
        var gold = new[] { "vec0", "float" };
        var vocab = new[] { "vec0", "float", "Redis" };
        var (p, r, _) = EvalScorer.EntityScores("VEC0 tables", gold, vocab);
        Assert.Equal(0.5, r, precision: 5); // float missing
        Assert.Equal(1.0, p);               // no distractor hits
    }

    [Fact]
    public void CitationExact_requires_gold_url_match()
    {
        var r = new QuestionResult("q1", "q?", 1, true,
            new[] { "https://github.com/asg017/sqlite-vec" }, "a");
        Assert.True(EvalScorer.CitationExact(r, "https://github.com/asg017/sqlite-vec/"));
        Assert.False(EvalScorer.CitationExact(r, "https://example.com"));
        var none = new QuestionResult("q1", "q?", 1, true, Array.Empty<string>(), "a");
        Assert.False(EvalScorer.CitationExact(none, "https://github.com/asg017/sqlite-vec"));
        Assert.False(EvalScorer.CitationExact(r, null));
    }

    [Fact]
    public void Percentile_nearest_rank_matches_frozen_definition()
    {
        var vals = new List<double> { 10, 20, 30, 40, 50 };
        Assert.Equal(30, EvalScorer.Percentile(vals, 0.50)); // nearest-rank: ceil(.5*5)=3 -> 3rd smallest = 30
        Assert.Equal(30, EvalScorer.Percentile(vals, 0.51));
        Assert.Equal(50, EvalScorer.Percentile(vals, 0.95));
        Assert.Equal(10, EvalScorer.Percentile(vals, 0.01));
    }
}
