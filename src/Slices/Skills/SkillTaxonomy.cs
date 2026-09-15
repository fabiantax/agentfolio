using System.Text.Json;
using AgentFolio.Contracts;

namespace Slices.Skills;

/// <summary>A derived skill with its evidence counts and urls.</summary>
public sealed record SkillStat(
    string Skill,
    int StarCount,
    int SaveCount,
    IReadOnlyList<string> EvidenceUrls);

/// <summary>
/// Pure, deterministic derivation of a skill taxonomy from stars + saves.
/// No I/O, no store knowledge — fully unit-testable.
/// </summary>
public static class SkillTaxonomy
{
    /// <summary>Small hardcoded stopword list for save-title keyword extraction.</summary>
    private static readonly HashSet<string> Stopwords = new()
    {
        "this", "that", "with", "from", "have", "what", "when", "your", "into",
        "about", "their", "them", "then", "than", "were", "been", "will", "would",
        "could", "should", "there", "these", "those", "some", "more", "most",
        "also", "just", "like", "over", "after", "before", "between", "which",
        "while", "where", "here", "only", "very", "much", "many", "such", "each",
        "every", "both", "other", "same", "next", "last", "first", "make", "made",
        "using", "used", "uses", "want", "need", "know", "tool", "tools",
    };

    /// <summary>Languages recognized when they appear as the whole Description.</summary>
    private static readonly HashSet<string> KnownLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "c++", "c#", "f#", "go", "rust", "python", "java", "kotlin", "swift",
        "typescript", "javascript", "ruby", "php", "scala", "haskell", "elixir",
        "clojure", "dart", "lua", "zig", "ocaml", "erlang", "perl", "r", "julia",
        "objective-c", "visual basic", "groovy", "solidity", "nim", "crystal",
    };

    public static IReadOnlyList<SkillStat> Derive(
        IReadOnlyList<StarredRepoRecord> stars,
        IReadOnlyList<SavedPostRecord> saves)
    {
        var starCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var saveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var star in stars)
        {
            var url = "https://github.com/" + star.FullName;
            foreach (var skill in SkillsFromStar(star))
            {
                starCounts[skill] = starCounts.TryGetValue(skill, out var n) ? n + 1 : 1;
                if (!evidence.TryGetValue(skill, out var list))
                    evidence[skill] = list = new List<string>();
                if (!list.Contains(url)) list.Add(url);
            }
        }

        foreach (var save in saves)
        {
            foreach (var skill in KeywordsFromSave(save))
            {
                saveCounts[skill] = saveCounts.TryGetValue(skill, out var n) ? n + 1 : 1;
                if (!evidence.TryGetValue(skill, out var list))
                    evidence[skill] = list = new List<string>();
                if (!list.Contains(save.CanonicalUrl)) list.Add(save.CanonicalUrl);
            }
        }

        return starCounts.Keys
            .Concat(saveCounts.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(skill => new SkillStat(
                skill,
                starCounts.TryGetValue(skill, out var s) ? s : 0,
                saveCounts.TryGetValue(skill, out var v) ? v : 0,
                evidence.TryGetValue(skill, out var urls) ? urls : new List<string>()))
            .OrderByDescending(x => x.StarCount + x.SaveCount)
            .ThenBy(x => x.Skill, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> SkillsFromStar(StarredRepoRecord star)
    {
        foreach (var topic in ParseTopics(star.TopicsJson))
        {
            var t = topic.Trim().ToLowerInvariant();
            if (t.Length > 0) yield return t;
        }

        // Primary language: only when Description is exactly a known language name.
        var desc = star.Description?.Trim();
        if (!string.IsNullOrEmpty(desc) && KnownLanguages.Contains(desc))
            yield return desc.ToLowerInvariant();
    }

    /// <summary>Safely deserialize TopicsJson as a JSON array of strings; never throws.</summary>
    private static IReadOnlyList<string> ParseTopics(string? topicsJson)
    {
        if (string.IsNullOrWhiteSpace(topicsJson)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(topicsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var topics = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String)
                    topics.Add(el.GetString() ?? string.Empty);
            return topics;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> KeywordsFromSave(SavedPostRecord save)
    {
        var title = save.Title;
        if (string.IsNullOrEmpty(title)) yield break;

        var word = new System.Text.StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsLetter(ch))
            {
                word.Append(ch);
            }
            else if (word.Length > 0)
            {
                var kw = word.ToString().ToLowerInvariant();
                word.Clear();
                if (kw.Length >= 4 && !Stopwords.Contains(kw)) yield return kw;
            }
        }

        if (word.Length > 0)
        {
            var kw = word.ToString().ToLowerInvariant();
            if (kw.Length >= 4 && !Stopwords.Contains(kw)) yield return kw;
        }
    }
}
