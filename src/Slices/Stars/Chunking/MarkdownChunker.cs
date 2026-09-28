namespace Slices.Stars.Chunking;

/// <summary>
/// One chunk of a README (#66 stage B): token-bounded slice with heading context.
/// Ids are deterministic (repoId:chunkIdx) so rebuild-from-raw is stable.
/// </summary>
public sealed record ReadmeChunk(
    long RepoId,
    int Index,
    string Text,
    string HeadingPath,
    int StartOffset,
    int EndOffset);

/// <summary>
/// Markdown-aware chunker (port of veld chunker.rs semantics, #70 stage B1):
/// ~512-token target with 10% overlap, split on headings/paragraphs, never
/// mid-word. Hard-caps a chunk at 4000 CHARS — GLiFormer returns zero entities
/// silently past ~4-5k effective tokens (measured 09-15); fail-loud > ceiling
/// is the guard the demo-stack shipped for the same reason.
/// </summary>
public static class MarkdownChunker
{
    public const int MaxChunkChars = 4000;
    private const int TargetTokens = 512;
    private const int OverlapTokens = 51; // ~10%
    // ~4 chars/token for prose+code mixed READMEs (measured on agentfolio/veld docs)
    private const int TargetChars = TargetTokens * 4;

    public static IReadOnlyList<ReadmeChunk> Chunk(long repoId, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Array.Empty<ReadmeChunk>();

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var chunks = new List<ReadmeChunk>();
        var headingPath = new List<string>();
        var current = new List<string>();
        var currentStart = 0;
        var offset = 0;

        void Flush()
        {
            if (current.Count == 0) return;
            var text = string.Join('\n', current).Trim();
            if (text.Length == 0) { current.Clear(); return; }
            // Hard guard: a single block larger than the ceiling gets split by
            // paragraphs; a single PARAGRAPH larger than the ceiling gets split
            // by sentences; still-oversized gets hard-cut (never silently dropped).
            foreach (var piece in SplitOversized(text))
            {
                var idx = chunks.Count;
                chunks.Add(new ReadmeChunk(repoId, idx, piece,
                    string.Join(" > ", headingPath), offset, offset + piece.Length));
                offset += piece.Length;
            }
            current.Clear();
        }

        foreach (var line in lines)
        {
            var heading = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (heading.Success)
            {
                Flush();
                var level = heading.Groups[1].Value.Length;
                var title = heading.Groups[2].Value.Trim();
                headingPath.RemoveRange(Math.Min(level - 1, headingPath.Count),
                    Math.Max(0, headingPath.Count - (level - 1)));
                if (headingPath.Count < level)
                    headingPath.AddRange(Enumerable.Repeat("", level - 1 - headingPath.Count));
                if (level - 1 < headingPath.Count) headingPath[level - 1] = title;
                else headingPath.Add(title);
                currentStart = offset;
                continue;
            }

            current.Add(line);
            if (string.IsNullOrWhiteSpace(line) && current.Sum(l => l.Length) >= TargetChars)
                Flush(); // paragraph boundary at target size
            else if (current.Sum(l => l.Length) >= MaxChunkChars)
                Flush(); // ceiling guard
            offset += line.Length + 1;
        }
        Flush();

        // Add 10% overlap: prepend the tail of the previous chunk (token-approx).
        for (var i = chunks.Count - 1; i > 0; i--)
        {
            var prev = chunks[i - 1].Text;
            var tail = prev.Length <= 200 ? prev : prev[^200..];
            var withOverlap = tail + "\n" + chunks[i].Text;
            if (withOverlap.Length <= MaxChunkChars)
                chunks[i] = chunks[i] with { Text = withOverlap };
        }

        // Re-index after overlap insertion.
        for (var i = 0; i < chunks.Count; i++)
            chunks[i] = chunks[i] with { Index = i };

        return chunks;
    }

    private static IEnumerable<string> SplitOversized(string text)
    {
        if (text.Length <= MaxChunkChars) { yield return text; yield break; }
        // Split on paragraph boundaries first.
        var paras = text.Split("\n\n");
        var buf = new List<string>();
        var bufLen = 0;
        foreach (var p in paras)
        {
            if (p.Length > MaxChunkChars)
            {
                // Sentence-split the monster paragraph.
                foreach (var s in SplitBySentences(p)) { yield return s; }
                continue;
            }
            if (bufLen + p.Length > MaxChunkChars && buf.Count > 0)
            {
                yield return string.Join("\n\n", buf);
                buf.Clear(); bufLen = 0;
            }
            buf.Add(p); bufLen += p.Length + 2;
        }
        if (buf.Count > 0) yield return string.Join("\n\n", buf);
    }

    private static IEnumerable<string> SplitBySentences(string text)
    {
        var buf = new System.Text.StringBuilder();
        foreach (var sentence in System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+"))
        {
            if (buf.Length + sentence.Length + 1 > MaxChunkChars && buf.Length > 0)
            {
                yield return buf.ToString();
                buf.Clear();
            }
            if (sentence.Length > MaxChunkChars)
            {
                // Last resort: hard cut (never drop content).
                for (var i = 0; i < sentence.Length; i += MaxChunkChars)
                    yield return sentence[i..Math.Min(i + MaxChunkChars, sentence.Length)];
                continue;
            }
            buf.Append(sentence).Append(' ');
        }
        if (buf.Length > 0) yield return buf.ToString();
    }
}
