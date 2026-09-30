using Slices.Stars.Chunking;
using Slices.Stars.Extraction;
using Slices.Stars.Graph;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#70 stage B5 + #89: ingest orchestration over fakes — chunk->NER->GF, both transports.</summary>
public class StarsGraphIngestTests
{
    /// <summary>Captures every Cypher statement instead of shipping it (legacy a2a path).</summary>
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

    /// <summary>Captures StarsIngestBatches instead of shipping them (sidecar path, no real process).</summary>
    private sealed class RecordingTransport : IGraphIngestTransport
    {
        public System.Collections.Generic.List<StarsIngestBatch> Batches { get; } = new();
        public IngestSummary? SummaryToReturn { get; set; }
        public Exception? ThrowOnShip { get; set; }
        public ulong LastIdBase { get; private set; }

        public System.Threading.Tasks.Task<IngestSummary?> ShipAsync(StarsIngestBatch batch, ulong sidecarIdBase = 0, System.Threading.CancellationToken ct = default)
        {
            if (ThrowOnShip is not null) throw ThrowOnShip;
            LastIdBase = sidecarIdBase;
            Batches.Add(batch);
            return System.Threading.Tasks.Task.FromResult(SummaryToReturn);
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
    public async Task Ingest_upserts_repo_chunks_and_next_links_legacy_a2a()
    {
        var gf = new RecordingGf();
        var svc = new StarsGraphIngestService(new A2aIngestTransport(gf), new FakeNer());
        var md = "# A\n\nalpha text\n\n## B\n\nbeta text";
        var stats = await svc.IngestRepoAsync(7, "o/n", "d", "C#", md, new System.Collections.Generic.Dictionary<long, string>());

        Assert.NotNull(stats);
        Assert.Equal(1, stats.Repos);
        Assert.True(stats.Chunks >= 2);
        Assert.Equal(stats.Chunks, stats.Entities); // FakeNer: 1 per chunk
        Assert.Contains(gf.Statements, s => s.Contains("MERGE (r:Repo {repoId: 7})"));
        Assert.Contains(gf.Statements, s => s.Contains("chunkId: '7:0'"));
        Assert.Contains(gf.Statements, s => s.Contains("MERGE (a)-[:next]->(b)"));
        Assert.Contains(gf.Statements, s => s.Contains("has_chunk"));
    }

    [Fact]
    public async Task Known_repo_mentions_become_edges_unknown_are_skipped_legacy_a2a()
    {
        var gf = new RecordingGf();
        var svc = new StarsGraphIngestService(new A2aIngestTransport(gf), new FakeNer());
        var names = new System.Collections.Generic.Dictionary<long, string> { [8] = "knowledgator/GLiFormer" };
        var md = "See https://github.com/knowledgator/GLiFormer and https://github.com/someone/unknown-repo.";
        var stats = await svc.IngestRepoAsync(7, "o/n", null, null, md, names);

        Assert.NotNull(stats);
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
        var svc = new StarsGraphIngestService(new A2aIngestTransport(gf), new FakeNer());
        var stats = await svc.IngestRepoAsync(3, "o/e", null, null, "", new System.Collections.Generic.Dictionary<long, string>());
        Assert.NotNull(stats);
        Assert.Equal(0, stats.Chunks);
        Assert.Contains(gf.Statements, s => s.Contains("MERGE (r:Repo {repoId: 3})"));
    }

    [Fact]
    public async Task Relex_relations_are_counted_when_extractor_provided()
    {
        var gf = new RecordingGf();
        var svc = new StarsGraphIngestService(new A2aIngestTransport(gf), new FakeNer(), new FakeRelex());
        var stats = await svc.IngestRepoAsync(5, "o/n", null, null, "# T\n\ntext", new System.Collections.Generic.Dictionary<long, string>());
        Assert.NotNull(stats);
        Assert.Equal(stats.Chunks, stats.Relations); // FakeRelex: 1 relation per chunk
    }

    // ---- #89: sidecar contract ----

    /// <summary>Pinned #89 wire contract, numeric-id revision (golden shape — changes here must be deliberate).
    /// Ids are a dense contiguous span allocated by the caller: repo node = idBase,
    /// chunk i = idBase+1+i (GF sizes CSR/adjacency tables at O(max node id)).
    /// External identity (repo:N, repo:N:i) lives in node names + properties.</summary>
    private static string ExpectedGoldenBatchJson =>
        "{\"batch_id\":\"stars-repo:7\",\"source\":\"agentfolio-stars\",\"base_id\":0," +
        "\"graph\":\"agentfolio_stars\",\"store_path\":\"agentfolio_stars\"," +
        "\"nodes\":[" +
        "{\"id\":41,\"node_type\":\"Repo\",\"name\":\"o/n\",\"properties\":{\"repoId\":7,\"fullName\":\"o/n\",\"description\":\"d\",\"language\":\"C#\"}}," +
        "{\"id\":42,\"node_type\":\"Chunk\",\"name\":\"repo:7:0\",\"properties\":{\"text\":\"alpha\",\"headingPath\":\"A\",\"repoId\":\"repo:7\",\"chunkId\":\"repo:7:0\"}}," +
        "{\"id\":43,\"node_type\":\"Chunk\",\"name\":\"repo:7:1\",\"properties\":{\"text\":\"beta\",\"headingPath\":\"A > B\",\"repoId\":\"repo:7\",\"chunkId\":\"repo:7:1\"}}" +
        "]," +
        "\"edges\":[" +
        "{\"source_id\":41,\"target_id\":42,\"edge_type\":\"has_chunk\",\"properties\":{}}," +
        "{\"source_id\":41,\"target_id\":43,\"edge_type\":\"has_chunk\",\"properties\":{}}," +
        "{\"source_id\":42,\"target_id\":43,\"edge_type\":\"next\",\"properties\":{}}" +
        "]}";

    [Fact]
    public async Task Batch_json_matches_pinned_sidecar_contract()
    {
        var transport = new RecordingTransport();
        var svc = new StarsGraphIngestService(transport, new FakeNer());
        var chunks = new[]
        {
            new ReadmeChunk(7, 0, "alpha", "A", 0, 5),
            new ReadmeChunk(7, 1, "beta", "A > B", 6, 10),
        };
        var batch = new StarsIngestBatch
        {
            RepoNodeId = "repo:7",
            FullName = "o/n",
            Description = "d",
            Language = "C#",
            Chunks = chunks,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            batch.ToIngestBatch(7, idBase: 41), SidecarJson.Options);

        Assert.Equal(ExpectedGoldenBatchJson, json);
    }

    [Fact]
    public void Batch_ids_form_dense_contiguous_span_from_idBase()
    {
        var batch = new StarsIngestBatch
        {
            RepoNodeId = "repo:7",
            FullName = "o/n",
            Chunks = new[]
            {
                new ReadmeChunk(7, 0, "alpha", "A", 0, 5),
                new ReadmeChunk(7, 1, "beta", "A > B", 6, 10),
            },
        };

        var ingest = batch.ToIngestBatch(7, idBase: 41);

        Assert.Equal(41UL, ingest.Nodes[0].Id);
        Assert.Equal(new ulong[] { 42, 43 }, ingest.Nodes.Skip(1).Select(n => n.Id));
        Assert.Equal(
            new[] { (41UL, 42UL), (41UL, 43UL), (42UL, 43UL) },
            ingest.Edges.Select(e => (e.SourceId, e.TargetId)));
    }

    [Fact]
    public async Task Sidecar_path_never_ships_mention_batches()
    {
        var transport = new RecordingTransport();
        var svc = new StarsGraphIngestService(transport, new FakeNer());
        var names = new System.Collections.Generic.Dictionary<long, string> { [8] = "knowledgator/GLiFormer" };
        var md = "See https://github.com/knowledgator/GLiFormer.";
        var stats = await svc.IngestRepoAsync(7, "o/n", null, null, md, names, sidecarIdBase: 41);

        Assert.NotNull(stats);
        Assert.Equal(0, stats.MentionEdges); // cross-batch mentions stay off the sidecar path
        Assert.Single(transport.Batches); // main batch only
        Assert.Equal(41UL, transport.LastIdBase); // allocated span rode through to the transport
    }

    [Fact]
    public async Task Sidecar_summary_ok_false_is_surfaced_as_stage_skip()
    {
        var transport = new RecordingTransport
        {
            SummaryToReturn = new IngestSummary("stars-repo:7", Ok: false, 0, 0, 0,
                new[] { new IngestError("validation", "unknown edge target repo:99") }),
        };
        var svc = new StarsGraphIngestService(transport, new FakeNer());
        var stats = await svc.IngestRepoAsync(7, "o/n", null, null, "# T\n\ntext", new System.Collections.Generic.Dictionary<long, string>());

        Assert.Null(stats); // ok=false -> degrade-soft skip, no throw
        Assert.Single(transport.Batches); // main batch shipped, mentions stage never ran
    }

    [Fact]
    public void Selection_gf_ingest_cli_set_uses_sidecar_unset_uses_legacy()
    {
        var legacyGf = new RecordingGf();
        try
        {
            Environment.SetEnvironmentVariable("GF_INGEST_CLI", "/usr/local/bin/graphfusion-ingest-cli");
            var picked = StarsIngestTransportRouting.Select(legacyGf);
            Assert.IsType<GraphFusionIngestCliTransport>(picked);

            Environment.SetEnvironmentVariable("GF_INGEST_CLI", null);
            var pickedLegacy = StarsIngestTransportRouting.Select(legacyGf);
            Assert.IsType<A2aIngestTransport>(pickedLegacy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GF_INGEST_CLI", null);
        }
    }

    [Fact]
    public async Task Ner_extraction_failure_skips_repo_not_the_slice()
    {
        var transport = new RecordingTransport();
        var flakyNer = new ThrowingNer();
        var svc = new StarsGraphIngestService(transport, flakyNer);

        // Degrade-soft boundary: extraction failure -> null (repo skipped this
        // run, stays unbooked), never an exception to the sync worker.
        var stats = await svc.IngestRepoAsync(7, "o/n", null, null, "# T\n\ntext", new System.Collections.Generic.Dictionary<long, string>());

        Assert.Null(stats);
        Assert.Empty(transport.Batches); // nothing shipped when NER died
    }

    /// <summary>NER that always fails (gliner cold-start / 503 shape).</summary>
    private sealed class ThrowingNer : IEntityExtractor
    {
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ExtractionResult>> ExtractAsync(
            System.Collections.Generic.IReadOnlyList<string> texts,
            System.Collections.Generic.IReadOnlyList<string> entityLabels,
            System.Threading.CancellationToken ct = default)
            => throw new System.Net.Http.HttpRequestException("503 (Service Unavailable)");
    }

    [Fact]
    public async Task Sidecar_process_failure_degrades_soft_no_throw()
    {
        var transport = new RecordingTransport { ThrowOnShip = new IngestCliException("spawn failed: no such file") };
        var svc = new StarsGraphIngestService(transport, new FakeNer());
        var stats = await svc.IngestRepoAsync(7, "o/n", null, null, "# T\n\ntext", new System.Collections.Generic.Dictionary<long, string>());

        Assert.Null(stats); // process failure -> null, not an exception
    }

    [Fact]
    public async Task Sidecar_transport_parses_canned_stdout_summary()
    {
        const string canned = "{\"batch_id\":\"b-1\",\"ok\":true,\"nodes\":3,\"local_edges\":2,\"cross_base_edges\":1,\"errors\":[]}";
        var transport = new GraphFusionIngestCliTransport("/fake/cli", runProcess: (path, stdin, ct) =>
        {
            Assert.Equal("/fake/cli", path);
            Assert.Contains("\"batch_id\":\"stars-repo:1\"", stdin);
            Assert.Contains("\"node_type\":\"Repo\"", stdin);
            return System.Threading.Tasks.Task.FromResult(
                new GraphFusionIngestCliTransport.IngestProcessResult(0, canned, ""));
        });
        var batch = new StarsIngestBatch { RepoNodeId = "repo:1", FullName = "o/r" };
        var summary = await transport.ShipAsync(batch);

        Assert.NotNull(summary);
        Assert.True(summary.Ok);
        Assert.Equal("b-1", summary.BatchId);
        Assert.Equal(3, summary.Nodes);
        Assert.Equal(2, summary.LocalEdges);
        Assert.Equal(1, summary.CrossBaseEdges);
        Assert.Empty(summary.Errors);
    }

    [Fact]
    public async Task Sidecar_transport_nonzero_exit_throws_for_the_degrade_boundary()
    {
        var transport = new GraphFusionIngestCliTransport("/fake/cli", runProcess: (path, stdin, ct) =>
            System.Threading.Tasks.Task.FromResult(
                new GraphFusionIngestCliTransport.IngestProcessResult(2, "", "store locked")));
        var batch = new StarsIngestBatch { RepoNodeId = "repo:1", FullName = "o/r" };

        var ex = await Assert.ThrowsAsync<IngestCliException>(() => transport.ShipAsync(batch));
        Assert.Contains("exited 2", ex.Message);
        Assert.Contains("store locked", ex.Message);
    }
}

/// <summary>The exact serializer settings the sidecar transport pins — tests serialize with the same.</summary>
public static class SidecarJson
{
    public static System.Text.Json.JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Keep >, <, & literal on the wire (STJ default escapes them; the pinned
        // #89 contract shape is plain UTF-8 JSON).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
