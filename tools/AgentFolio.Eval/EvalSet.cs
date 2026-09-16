using System.Text.Json;

namespace AgentFolio.Eval;

/// <summary>
/// Frozen eval set (issue #44): real public sources + questions with gold
/// entities. Loaded from eval/sets/*.json; the set is COMMITTED and never
/// edited after a results file references it (pre-registration discipline).
/// </summary>
public sealed record EvalSource(string Id, string Title, string Url, string Text);

public sealed record EvalQuestion(string Id, string SourceId, string Question, IReadOnlyList<string> GoldEntities);

public sealed record EvalSet(
    string Id,
    string Created,
    IReadOnlyList<EvalSource> Sources,
    IReadOnlyList<string> DistractorEntities,
    IReadOnlyList<EvalQuestion> Questions)
{
    public static EvalSet Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var sources = root.GetProperty("sources").EnumerateArray()
            .Select(s => new EvalSource(
                s.GetProperty("id").GetString()!,
                s.GetProperty("title").GetString()!,
                s.GetProperty("url").GetString()!,
                s.GetProperty("text").GetString()!))
            .ToList();
        var distractors = root.GetProperty("distractorEntities").EnumerateArray()
            .Select(d => d.GetString()!).ToList();
        var questions = root.GetProperty("questions").EnumerateArray()
            .Select(q => new EvalQuestion(
                q.GetProperty("id").GetString()!,
                q.GetProperty("sourceId").GetString()!,
                q.GetProperty("question").GetString()!,
                q.GetProperty("goldEntities").EnumerateArray().Select(g => g.GetString()!).ToList()))
            .ToList();
        return new EvalSet(
            root.GetProperty("id").GetString()!,
            root.GetProperty("created").GetString()!,
            sources, distractors, questions);
    }

    public string? SourceUrlFor(EvalQuestion q) => Sources.FirstOrDefault(s => s.Id == q.SourceId)?.Url;
}

/// <summary>Raw per-question outcome from one harness run.</summary>
public sealed record QuestionResult(
    string Id,
    string Question,
    double LatencyMs,
    bool HasAnswer,
    IReadOnlyList<string> CitationUrls,
    string AnswerText);

/// <summary>Frozen metric definitions (see eval/README.md — do not change per run).</summary>
public static class EvalScorer
{
    /// <summary>
    /// Entity vocabulary = all gold entities across the set + distractor entities.
    /// Mentions are case-insensitive ordinal substring hits of vocabulary strings
    /// in the answer text. Precision counts hits of THIS question's gold entities
    /// over all vocabulary hits (parroting other questions' entities is punished).
    /// </summary>
    public static (double precision, double recall, double f1) EntityScores(
        string answerText, IReadOnlyList<string> gold, IReadOnlyList<string> vocabulary)
    {
        var hits = 0;
        var goldHits = 0;
        foreach (var entity in vocabulary.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = answerText.Contains(entity, StringComparison.OrdinalIgnoreCase);
            if (!found) continue;
            hits++;
            if (gold.Any(g => g.Equals(entity, StringComparison.OrdinalIgnoreCase))) goldHits++;
        }
        // Also count gold entities not in the vocabulary (defensive: set bug) as misses.
        foreach (var g in gold)
            if (!vocabulary.Contains(g, StringComparer.OrdinalIgnoreCase)
                && !answerText.Contains(g, StringComparison.OrdinalIgnoreCase))
            {
                // not found, not in vocab: recall miss already implicit (goldHits unchanged)
            }
        var recall = gold.Count == 0 ? 1.0 : (double)gold.Count(g => answerText.Contains(g, StringComparison.OrdinalIgnoreCase)) / gold.Count;
        var precision = hits == 0 ? 0.0 : (double)goldHits / hits;
        var f1 = precision + recall == 0 ? 0.0 : 2 * precision * recall / (precision + recall);
        return (precision, recall, f1);
    }

    /// <summary>Citation-exact: at least one citation URL equals the question's gold source URL.</summary>
    public static bool CitationExact(QuestionResult r, string? goldUrl)
    {
        if (goldUrl is null) return false;
        return r.CitationUrls.Any(u => u.TrimEnd('/').Equals(goldUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Nearest-rank percentile over a non-empty sample (frozen formula).</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var idx = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }
}
