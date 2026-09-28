namespace Slices.Stars.Graph;

using AgentFolio.Contracts;
using Slices.Stars.Chunking;
using Slices.Stars.Extraction;

/// <summary>
/// B5 orchestration (#66): README chunks -> NER nodes + relation edges -> GraphFusion.
/// Composes the landed units: MarkdownChunker (B1), IEntityExtractor (B2),
/// IRelationExtractor (B3), GraphFusionClient (B4). Mentions edge source: GitHub
/// repo URLs found in README text (closed-schema extraction, structure()-class task).
/// </summary>
public sealed class StarsGraphIngestService(
    GraphFusionClient gf,
    IEntityExtractor entityExtractor,
    IRelationExtractor? relationExtractor = null,
    int maxChunksPerRun = 200)
{
    /// <summary>GitHub repo URL pattern for mentions edges (html_url shapes only).</summary>
    private static readonly System.Text.RegularExpressions.Regex RepoUrl =
        new(@"github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public sealed record RunStats(int Repos, int Chunks, int Entities, int Relations, int MentionEdges);

    /// <summary>
    /// Ingest one repo's README: chunk -> NER batch -> GF upserts. Relations are
    /// optional (Relex). Known-repo mentions become edges; unknown targets are
    /// skipped at ingest (edge lands if the target star arrives later — the next
    /// full run re-walks mentions).
    /// </summary>
    public async Task<RunStats> IngestRepoAsync(
        long repoId, string fullName, string? description, string? language,
        string readmeText, IReadOnlyDictionary<long, string> repoNamesToIds,
        CancellationToken ct = default)
    {
        var chunks = MarkdownChunker.Chunk(repoId, readmeText);
        if (chunks.Count > maxChunksPerRun)
            chunks = chunks.Take(maxChunksPerRun).ToArray(); // bounded run; the rest lands next sync
        if (chunks.Count == 0)
        {
            await gf.ExecuteAsync(StarsGraphCypher.UpsertRepo(repoId, fullName, description, language), ct).ConfigureAwait(false);
            return new RunStats(1, 0, 0, 0, 0);
        }

        // NER over all chunks in ONE batched call (batching is the throughput lever).
        var labels = new[] { "software", "organization", "person", "concept", "library", "language" };
        var texts = chunks.Select(c => c.Text).ToArray();
        var ner = await entityExtractor.ExtractAsync(texts, labels, ct).ConfigureAwait(false);

        // Relations per chunk (prose default = Relex), best-effort.
        var rels = new ExtractionResult[chunks.Count];
        if (relationExtractor is not null)
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                try
                {
                    var one = await relationExtractor.ExtractRelationsAsync(
                        new[] { chunks[i].Text }, labels,
                        new[] { "integrates with", "developed by", "alternative to", "built on" }, ct).ConfigureAwait(false);
                    rels[i] = one.Count > 0 ? one[0] : new ExtractionResult(Array.Empty<ExtractedEntity>(), Array.Empty<ExtractedRelation>());
                }
                catch { rels[i] = new ExtractionResult(Array.Empty<ExtractedEntity>(), Array.Empty<ExtractedRelation>()); }
            }
        }

        // Upsert repo + chunks + next links.
        await gf.UpsertRepoWithChunksAsync(repoId, fullName, description, language, chunks, ct).ConfigureAwait(false);

        // Mentions: GitHub URLs in README text -> mentions_repo edges (known targets only).
        var mentionEdges = 0;
        foreach (var m in RepoUrl.Matches(readmeText).Cast<System.Text.RegularExpressions.Match>())
        {
            var name = m.Groups[2].Value;
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            var url = $"{m.Groups[1].Value}/{name}";
            foreach (var (otherId, otherName) in repoNamesToIds)
            {
                if (otherId != repoId && string.Equals(otherName, url, StringComparison.OrdinalIgnoreCase))
                {
                    await gf.ExecuteAsync(StarsGraphCypher.LinkMention(repoId, otherId), ct).ConfigureAwait(false);
                    mentionEdges++;
                    break;
                }
            }
        }

        var entityCount = ner.Sum(r => r.Entities.Count);
        var relationCount = rels.Sum(r => r?.Relations.Count ?? 0);
        return new RunStats(1, chunks.Count, entityCount, relationCount, mentionEdges);
    }
}
