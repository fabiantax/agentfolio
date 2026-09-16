using System.Text;
using AgentFolio.Eval;

namespace AgentFolio.Eval;

/// <summary>
/// AgentFolio eval CLI (issue #44, obligation O2):
///   dotnet run --project tools/AgentFolio.Eval -- --set eval/sets/v1.json --out eval/results/v1.md
/// Model config: --model/--api-base/--api-key flags, falling back to
/// NIM_MODEL / NIM_BASE_URL / NIM_API_KEY environment variables.
/// (Explicit class — a top-level Program would collide with AgentFolio.Api's
/// Program in WebApplicationFactory&lt;Program&gt;.)
/// </summary>
public static class EvalCli
{
    public static async Task<int> Main(string[] args)
    {
        var setPath = Arg(args, "--set") ?? "eval/sets/v1.json";
        var outPath = Arg(args, "--out") ?? "eval/results/v1.md";
        var model = Arg(args, "--model") ?? Environment.GetEnvironmentVariable("NIM_MODEL") ?? "nemotron-4b";
        var apiBase = Arg(args, "--api-base") ?? Environment.GetEnvironmentVariable("NIM_BASE_URL") ?? "http://127.0.0.1:8007/v1";
        var apiKey = Arg(args, "--api-key") ?? Environment.GetEnvironmentVariable("NIM_API_KEY") ?? "eval-local";

        if (!File.Exists(setPath)) { Console.Error.WriteLine($"eval set not found: {setPath}"); return 2; }
        var set = EvalSet.Load(setPath);
        Console.WriteLine($"eval set {set.Id} (created {set.Created}): {set.Sources.Count} sources, {set.Questions.Count} questions");
        Console.WriteLine($"model: {model} @ {apiBase}");

        using var harness = new ChatHarness();
        var client = harness.Boot(apiBase, apiKey, model);
        Console.WriteLine("app booted (real pipeline, real extraction); seeding sources…");

        foreach (var s in set.Sources)
        {
            await harness.SeedSourceAsync(client, s);
            Console.WriteLine($"  seeded {s.Id}: {s.Title}");
        }

        Console.WriteLine("asking questions…");
        var results = new List<QuestionResult>();
        foreach (var q in set.Questions)
        {
            var r = await ChatHarness.AskAsync(client, q, set.SourceUrlFor(q));
            results.Add(r);
            Console.WriteLine($"  {q.Id}: {(r.HasAnswer ? "answer" : "EMPTY")} in {r.LatencyMs:F0} ms, {r.CitationUrls.Count} citation(s)");
        }

        // ---- Frozen metric definitions (eval/README.md) ----
        var vocabulary = set.Questions.SelectMany(q => q.GoldEntities)
            .Concat(set.DistractorEntities)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var citationExact = 0;
        var hasCitation = 0;
        var perQuestion = new List<(EvalQuestion q, QuestionResult r, double p, double rec, double f1, bool citExact)>();
        foreach (var q in set.Questions)
        {
            var r = results.Single(x => x.Id == q.Id);
            if (r.CitationUrls.Count > 0) hasCitation++;
            var citExact = EvalScorer.CitationExact(r, set.SourceUrlFor(q));
            if (citExact) citationExact++;
            var (p, rec, f1) = EvalScorer.EntityScores(r.AnswerText, q.GoldEntities, vocabulary);
            perQuestion.Add((q, r, p, rec, f1, citExact));
        }

        var n = set.Questions.Count;
        var macroP = perQuestion.Sum(x => x.p) / n;
        var macroR = perQuestion.Sum(x => x.rec) / n;
        var macroF1 = perQuestion.Sum(x => x.f1) / n;
        var latencies = results.Select(r => r.LatencyMs).OrderBy(v => v).ToList();

        var md = new StringBuilder();
        md.AppendLine($"# AgentFolio eval results — set {set.Id}");
        md.AppendLine();
        md.AppendLine($"- run: {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        md.AppendLine($"- model: `{model}` @ `{apiBase}`");
        md.AppendLine("- pipeline: real Program (upload -> sandbox extract -> /chat SSE); no fakes");
        md.AppendLine();
        md.AppendLine("## Metrics (definitions frozen in eval/README.md)");
        md.AppendLine();
        md.AppendLine($"- `citation_exactness` — **{(double)citationExact / n:P1}** ({citationExact}/{n}): >=1 citation URL equals the gold source URL");
        md.AppendLine($"- `answer_has_citation_rate` — **{(double)hasCitation / n:P1}** ({hasCitation}/{n})");
        md.AppendLine($"- `ner_f1` (macro over questions, substring-vocab match) — **{macroF1:F3}** (P {macroP:F3} / R {macroR:F3})");
        md.AppendLine($"- `latency_p50_ms` — **{EvalScorer.Percentile(latencies, 0.50):F0}**; `latency_p95_ms` — **{EvalScorer.Percentile(latencies, 0.95):F0}**");
        md.AppendLine();
        md.AppendLine("## Per-question");
        md.AppendLine();
        md.AppendLine("| id | citation-exact | entity P/R/F1 | latency ms |");
        md.AppendLine("|---|---|---|---|");
        foreach (var (q, r, p, rec, f1, citExact) in perQuestion)
            md.AppendLine($"| {q.Id} | {(citExact ? "yes" : "no")} | {p:F2}/{rec:F2}/{f1:F2} | {r.LatencyMs:F0} |");
        md.AppendLine();
        md.AppendLine("## Notes");
        md.AppendLine();
        md.AppendLine("- Empty-retrieval / no-citation answers count AGAINST the rates above (never as pass).");
        md.AppendLine("- Known state at first run: /chat emits `citations: []` (retrieval wiring pending, ChatEndpoints.cs) —");
        md.AppendLine("  citation_exactness 0% is the honest baseline this harness exists to publish.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, md.ToString());
        Console.WriteLine($"results written: {outPath}");
        Console.WriteLine($"citation_exactness={(double)citationExact / n:P1} has_citation={(double)hasCitation / n:P1} ner_f1={macroF1:F3} p50={EvalScorer.Percentile(latencies, 0.50):F0}ms p95={EvalScorer.Percentile(latencies, 0.95):F0}ms");
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
