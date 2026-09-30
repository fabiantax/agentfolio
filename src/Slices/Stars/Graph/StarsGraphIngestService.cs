namespace Slices.Stars.Graph;

using AgentFolio.Contracts;
using Microsoft.Extensions.Logging;
using Slices.Stars.Chunking;
using Slices.Stars.Extraction;

/// <summary>
/// B5 orchestration (#66): README chunks -> NER nodes + relation edges -> GraphFusion.
/// Composes the landed units: MarkdownChunker (B1), IEntityExtractor (B2),
/// IRelationExtractor (B3), IGraphIngestTransport (#89: gf-a2a legacy or
/// graphfusion-ingest-cli sidecar, selected by GF_INGEST_CLI). Mentions edge
/// source: GitHub repo URLs found in README text (closed-schema extraction,
/// structure()-class task). Transport failures degrade soft: the stage is
/// skipped for the run, no exception reaches the sync worker.
/// </summary>
public sealed class StarsGraphIngestService(
    IGraphIngestTransport transport,
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
    /// full run re-walks mentions). Returns null when the transport failed
    /// (sidecar missing/failing/ok=false) — the caller skips the stage exactly
    /// like today's 422 degrade-soft path.
    /// </summary>
    public async Task<RunStats?> IngestRepoAsync(
        long repoId, string fullName, string? description, string? language,
        string readmeText, IReadOnlyDictionary<long, string> repoNamesToIds,
        ulong sidecarIdBase = 0, CancellationToken ct = default)
    {
        var chunks = MarkdownChunker.Chunk(repoId, readmeText);
        if (chunks.Count > maxChunksPerRun)
            chunks = chunks.Take(maxChunksPerRun).ToArray(); // bounded run; the rest lands next sync
        if (chunks.Count == 0)
        {
            var repoOnly = new StarsIngestBatch
            {
                RepoNodeId = StarsGraphIds.Repo(repoId),
                FullName = fullName,
                Description = description,
                Language = language,
                Chunks = Array.Empty<Chunking.ReadmeChunk>(),
            };
            return await ShipAsync(repoOnly, sidecarIdBase, stats => new RunStats(1, 0, 0, 0, 0), ct).ConfigureAwait(false);
        }

        // NER over all chunks in ONE batched call (batching is the throughput lever).
        // Per-repo guard: extraction failures (gliner socket idle-unload cold start,
        // 503/timeout) must kill THIS repo, not the worker's whole 20-repo slice —
        // the repo stays unbooked and retries next sync.
        var labels = new[] { "software", "organization", "person", "concept", "library", "language" };
        var texts = chunks.Select(c => c.Text).ToArray();
        IReadOnlyList<ExtractionResult> ner;
        try
        {
            ner = await entityExtractor.ExtractAsync(texts, labels, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception nex)
        {
            Console.Error.WriteLine($"stars ingest: NER extraction failed for repo {repoId} — skipping repo this run ({nex.Message})");
            return null;
        }

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

        // Batch upsert: repo + chunks + has_chunk/next links in one transport ship.
        var batch = new StarsIngestBatch
        {
            RepoNodeId = StarsGraphIds.Repo(repoId),
            FullName = fullName,
            Description = description,
            Language = language,
            Chunks = chunks,
        };
        var entityCount = ner.Sum(r => r.Entities.Count);
        var relationCount = rels.Sum(r => r?.Relations.Count ?? 0);
        var mainStats = await ShipAsync(
            batch,
            sidecarIdBase,
            summary => new RunStats(1, chunks.Count, entityCount, relationCount, summary?.LocalEdges ?? 0),
            ct).ConfigureAwait(false);
        if (mainStats is null)
            return null;

        // Mentions: GitHub URLs in README text -> mentions_repo edges (known targets
        // only). Legacy a2a transport only: the sidecar CLI validates edge endpoints
        // in-batch and raw repo ids are meaningless in the dense id space, so the
        // sidecar path never ships them (until GF grows cross-batch edge support).
        var mentionEdges = 0;
        var mentions = new List<(long From, long To)>();
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
                    mentions.Add((repoId, otherId));
                    mentionEdges++;
                    break;
                }
            }
        }
        if (mentions.Count > 0 && transport is A2aIngestTransport)
        {
            var mentionBatch = new StarsIngestBatch
            {
                RepoNodeId = StarsGraphIds.Repo(repoId),
                FullName = fullName,
                Description = description,
                Language = language,
                Chunks = Array.Empty<Chunking.ReadmeChunk>(),
                Mentions = mentions,
            };
            var mentionStats = await ShipAsync(
                mentionBatch,
                0, // legacy transport; id base unused
                summary => new RunStats(0, 0, 0, 0, mentionEdges),
                ct).ConfigureAwait(false);
            if (mentionStats is null)
                mentionEdges = 0;
        }

        return new RunStats(1, chunks.Count, entityCount, relationCount, mentionEdges);
    }

    /// <summary>
    /// Degrade-soft boundary: any transport failure (legacy HTTP throw, sidecar
    /// spawn/timeout/exit/parse failure, ok=false summary) logs and returns null —
    /// never throws to the sync worker. sidecarIdBase is the dense node-id span
    /// allocated for this batch by the caller (no-op for the legacy transport).
    /// </summary>
    private async Task<RunStats?> ShipAsync(StarsIngestBatch batch, ulong sidecarIdBase, Func<IngestSummary?, RunStats> project, CancellationToken ct)
    {
        try
        {
            var summary = await transport.ShipAsync(batch, sidecarIdBase, ct).ConfigureAwait(false);
            if (summary is { Ok: false })
            {
                var detail = string.Join("; ", summary.Errors.Select(e => $"{e.Kind}: {e.Message}"));
                Console.Error.WriteLine($"stars ingest: sidecar batch {summary.BatchId} not ok — skipping stage ({detail})");
                return null;
            }
            return project(summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"stars ingest: transport failed — skipping stage ({ex.Message})");
            return null;
        }
    }
}

/// <summary>Deterministic node id forms shared by both transports.</summary>
public static class StarsGraphIds
{
    public static string Repo(long repoId) => $"repo:{repoId}";
}

/// <summary>Env-driven transport routing (GF_INGEST_CLI set -> sidecar, unset -> legacy a2a).</summary>
public static class StarsIngestTransportRouting
{
    public static IGraphIngestTransport Select(GraphFusionClient? legacy, Microsoft.Extensions.Logging.ILogger? log = null)
    {
        var cliPath = Environment.GetEnvironmentVariable("GF_INGEST_CLI");
        if (!string.IsNullOrWhiteSpace(cliPath))
        {
            log?.LogInformation("stars ingest transport: graphfusion-ingest-cli sidecar at {CliPath}", cliPath);
            return new GraphFusionIngestCliTransport(cliPath, log);
        }
        if (legacy is null)
            throw new InvalidOperationException("stars ingest: GF_INGEST_CLI unset and no gf-a2a client configured");
        log?.LogInformation("stars ingest transport: legacy gf-a2a HTTP (GF_INGEST_CLI unset)");
        return new A2aIngestTransport(legacy);
    }
}
