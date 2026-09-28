using Slices.Stars.Chunking;
using Slices.Stars.Extraction;
using Slices.Stars.Graph;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#70 stage B5: ingest orchestration over fakes — chunk->NER->GF upserts, mentions edges.</summary>
public class StarsGraphIngestTests
{
    /// <summary>Captures every Cypher statement instead of shipping it.</summary>
    private sealed class RecordingGf : GraphFusionClient
    {
        public RecordingGf() : base("http://127.0.0.1:1") { }
        public System.Collections.Generic.List<string> Statements { get; } = new();

        public override System.Threading.Tasks.Task ExecuteAsync(string cypher, System.Threading.CancellationToken ct = default)
        {
            Statements.Add(cypher);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>Deterministic NER: one Software entity per chunk.</summary>
    private sealed class FakeNer : IEntityExtractor
    {
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ExtractionResult>> ExtractAsync(
            System.Collections.Generic.IReadOnlyList<string> texts,
            System.Collections.Generic.IReadOnlyList<string> entityLabels,
            System.Threading.CancellationToken ct = default)
        {
            var results = new System.Collections.Generic.List<ExtractionResult>();
            foreach (var t in texts)
                results.Add(new ExtractionResult(
                    new[] { new ExtractedEntity(t[..Math.Min(10, t.Length)], "software", 0, 10, 0.9) },
                    Array.Empty<ExtractedRelation>()));
            return System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<ExtractionResult>>(results);
        }
    }

    private sealed class FakeRelex : IRelationExtractor
    {
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ExtractionResult>> ExtractRelationsAsync(
            System.Collections.Generic.IReadOnlyList<string> texts,
            System.Collections.Generic.IReadOnlyList<string> entityLabels,
            System.Collections.Generic.IReadOnlyList<string> relationLabels,
            System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<ExtractionResult>>(
                texts.Select(t => new ExtractionResult(
                    Array.Empty<ExtractedEntity>(),
                    new[] { new ExtractedRelation("A", "software", "B", "software", "integrates with", 0.9) })).ToArray());
    }

    [Fact]
    public async Task Ingest_upserts_repo_chunks_and_next_links()
    {
        var gf = new RecordingGf();
        var svc = new StarsGraphIngestService(gf, new FakeNer());
        var md = "# A\n\nalpha text\n\n## B\n\nbeta text";
        var stats = await svc.IngestRepoAsync(7, "o/n", "d", "C#", md, new System.Collections.Generic.Dictionary<long, string>());

        Assert.Equal(1, stats.Repos);
        Assert.True(stats.Chunks >= 2);
        Assert.Equal(stats.Chunks, stats.Entities); // FakeNer: 1 per chunk
        Assert.Contains(gf.Statements, s => s.Contains("MERGE (r:Repo {repoId: 7})"));
        Assert.Contains(gf.Statements, s => s.Contains("chunkId: '7:0'"));
        Assert.Contains(gf.Statements, s => s.Contains("MERGE (a)-[:next]->(b)"));
        Assert.Contains(gf.Statements, s => s.Contains("has_chunk"));
    }

    [Fact]
    public async Task Known_repo_mentions_become_edges_unknown_are_skipped()
    {
        var gf = new RecordingGf();
        var svc = new StarsGraphIngestService(gf, new FakeNer());
        var names = new System.Collections.Generic.Dictionary<long, string> { [8] = "knowledgator/GLiFormer" };
        var md = "See https://github.com/knowledgator/GLiFormer and https://github.com/someone/unknown-repo.";
        var stats = await svc.IngestRepoAsync(7, "o/n", null, null, md, names);

        Assert.Equal(1, stats.MentionEdges);
        Assert.Contains(gf.Statements, s => s.Contains("mentions_repo") && s.Contains("repoId: 8"));
        Assert.DoesNotContain(gf.Statements, s =>
            s.Contains("mentions_repo") &&
            System.Text.RegularExpressions.Regex.IsMatch(s, @"repoId: 7\}.*repoId: 7\}"));
    }

    [Fact]
    public async Task Empty_readme_still_upserts_the_repo_node()
    {
        var gf = new RecordingGf();
        var svc = new StarsGraphIngestService(gf, new FakeNer());
        var stats = await svc.IngestRepoAsync(3, "o/e", null, null, "", new System.Collections.Generic.Dictionary<long, string>());
        Assert.Equal(0, stats.Chunks);
        Assert.Contains(gf.Statements, s => s.Contains("MERGE (r:Repo {repoId: 3})"));
    }

    [Fact]
    public async Task Relex_relations_are_counted_when_extractor_provided()
    {
        var gf = new RecordingGf();
        var svc = new StarsGraphIngestService(gf, new FakeNer(), new FakeRelex());
        var stats = await svc.IngestRepoAsync(5, "o/n", null, null, "# T\n\ntext", new System.Collections.Generic.Dictionary<long, string>());
        Assert.Equal(stats.Chunks, stats.Relations); // FakeRelex: 1 relation per chunk
    }
}
