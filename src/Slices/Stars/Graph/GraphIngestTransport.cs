namespace Slices.Stars.Graph;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// One batch entry for the graphfusion-ingest-cli sidecar (#89 contract, numeric-id
/// revision). The first real sync (job 0f44f597) 422'd every batch: the GF CLI
/// demands base_id:u8 and u64 node/edge ids — string ids like "repo:7" never parse.
/// External identity (repo:N, repo:N:i) rides in properties only, because
/// GraphStorage renumbers ids dense 0..n on open (compact_nodes finding).
/// Batch: {"batch_id","source","base_id","graph","store_path","nodes","edges"}.
/// Summary (stdout): {"batch_id","ok","nodes","local_edges","cross_base_edges","errors"}.
/// </summary>
public sealed record IngestNode(ulong Id, string NodeType, string Name, Dictionary<string, object?> Properties);

public sealed record IngestEdge(ulong SourceId, ulong TargetId, string EdgeType, Dictionary<string, object?> Properties);

public sealed record IngestBatch(
    string BatchId,
    string Source,
    byte BaseId,
    string Graph,
    string StorePath,
    IReadOnlyList<IngestNode> Nodes,
    IReadOnlyList<IngestEdge> Edges);

public sealed record IngestError(string Kind, string Message);

public sealed record IngestSummary(
    string BatchId,
    bool Ok,
    int Nodes,
    int LocalEdges,
    int CrossBaseEdges,
    IReadOnlyList<IngestError> Errors);

/// <summary>
/// Destination for stars-graph mutations (#89): either the legacy gf-a2a HTTP
/// Cypher path (422 by design for mutations, degrades soft today) or the
/// graphfusion-ingest-cli sidecar. One StarsIngestBatch maps a repo's whole
/// upsert set (repo + chunks + has_chunk/next + mentions_repo).
/// </summary>
public interface IGraphIngestTransport
{
    Task<IngestSummary?> ShipAsync(StarsIngestBatch batch, ulong sidecarIdBase = 0, CancellationToken ct = default);
}

/// <summary>
/// The full mutation set for one repo ingest run, expressed structurally so the
/// sidecar transport can serialize it against the pinned #89 contract.
/// Null = stage not applicable (degrade-soft skips without shipping).
/// </summary>
public sealed class StarsIngestBatch
{
    public required string RepoNodeId { get; init; }
    public required string FullName { get; init; }
    public string? Description { get; init; }
    public string? Language { get; init; }
    public IReadOnlyList<Chunking.ReadmeChunk>? Chunks { get; init; }
    public IReadOnlyList<(long FromRepoId, long ToRepoId)>? Mentions { get; init; }

    /// <summary>
    /// Build the node/edge payload for the numeric-id sidecar contract. GF sizes
    /// CSR/adjacency/embedding tables at O(max node id) (database.rs train/cleora
    /// paths), so ids MUST stay dense across batches: the caller allocates a
    /// contiguous span [idBase, idBase+1+chunks) from the persistent id-book
    /// (stars_graph_nodes). Layout: repo node = idBase, chunk i = idBase+1+i.
    /// External identity (repo:N, repo:N:i) rides in node name + properties only —
    /// GraphStorage renumbers ids on open (compact_nodes finding).
    /// Mentions are NOT serialized here: the CLI validates edge endpoints in-batch
    /// and raw repo ids are meaningless in the dense id space (they collide with
    /// chunk ids); mentions stay on the legacy a2a transport until GF supports
    /// cross-batch edges.
    /// </summary>
    public IngestBatch ToIngestBatch(long repoId, ulong idBase)
    {
        var nodes = new List<IngestNode>
        {
            new(idBase, "Repo", FullName, new Dictionary<string, object?>
            {
                ["repoId"] = repoId,
                ["fullName"] = FullName,
                ["description"] = Description,
                ["language"] = Language,
            })
        };
        var edges = new List<IngestEdge>();
        if (Chunks is not null)
        {
            foreach (var c in Chunks)
            {
                var chunkNum = idBase + 1UL + Convert.ToUInt64(c.Index);
                var chunkId = $"{RepoNodeId}:{c.Index}"; // external identity lives in properties/name
                nodes.Add(new IngestNode(chunkNum, "Chunk", chunkId, new Dictionary<string, object?>
                {
                    ["text"] = c.Text,
                    ["headingPath"] = c.HeadingPath,
                    ["repoId"] = RepoNodeId,
                    ["chunkId"] = chunkId,
                }));
                edges.Add(new IngestEdge(idBase, chunkNum, "has_chunk", new Dictionary<string, object?>()));
                if (c.Index > 0)
                    edges.Add(new IngestEdge(chunkNum - 1, chunkNum, "next", new Dictionary<string, object?>()));
            }
        }
        return new IngestBatch(
            BatchId: $"stars-{RepoNodeId}",
            Source: "agentfolio-stars",
            BaseId: 0,
            Graph: "agentfolio_stars",
            StorePath: Environment.GetEnvironmentVariable("GF_INGEST_STORE_PATH") ?? "agentfolio_stars",
            Nodes: nodes,
            Edges: edges);
    }
}

/// <summary>
/// Transport #1 (legacy, unchanged behavior): ships Cypher over gf-a2a HTTP.
/// The A2A endpoint 422s mutations by design (a2a_handler.rs:414) — callers
/// degrade soft on the throw, exactly as before #89.
/// </summary>
public sealed class A2aIngestTransport(GraphFusionClient client) : IGraphIngestTransport
{
    public async Task<IngestSummary?> ShipAsync(StarsIngestBatch batch, ulong sidecarIdBase = 0, CancellationToken ct = default)
    {
        await client.ExecuteAsync(StarsGraphCypher.UpsertRepo(ExtractRepoId(batch.RepoNodeId), batch.FullName, batch.Description, batch.Language), ct).ConfigureAwait(false);
        if (batch.Chunks is not null)
        {
            foreach (var c in batch.Chunks)
            {
                await client.ExecuteAsync(StarsGraphCypher.UpsertChunk(ExtractRepoId(batch.RepoNodeId), c.Index, c.Text, c.HeadingPath), ct).ConfigureAwait(false);
                if (c.Index > 0)
                    await client.ExecuteAsync(StarsGraphCypher.LinkNextChunk(ExtractRepoId(batch.RepoNodeId), c.Index), ct).ConfigureAwait(false);
            }
        }
        if (batch.Mentions is not null)
        {
            foreach (var (fromId, toId) in batch.Mentions)
                await client.ExecuteAsync(StarsGraphCypher.LinkMention(fromId, toId), ct).ConfigureAwait(false);
        }
        return null; // legacy path has no sidecar summary to surface
    }

    /// <summary>"repo:123" -> 123 (repo node ids are the only repo: form).</summary>
    internal static long ExtractRepoId(string nodeId)
    {
        var sep = nodeId.IndexOf(':');
        return sep < 0 ? long.Parse(nodeId) : long.Parse(nodeId[(sep + 1)..]);
    }
}

/// <summary>
/// Transport #2 (#89): serialize the batch against the pinned sidecar contract,
/// write to the graphfusion-ingest-cli process stdin, parse the stdout summary.
/// 60s hard timeout; non-zero exit / unparsable stdout / ok=false all surface as
/// failures the caller degrades soft on (no exception escapes ShipAsync's caller
/// contract — the service maps them to a skip).
/// </summary>
public sealed class GraphFusionIngestCliTransport : IGraphIngestTransport
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Keep >, <, & literal on the wire (STJ default escapes them as \u003E…;
        // the pinned #89 contract is plain UTF-8 JSON, matched by the golden test).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _cliPath;
    private readonly ILogger? _log;
    private readonly TimeSpan _timeout;
    private readonly Func<string, string, CancellationToken, Task<IngestProcessResult>> _runProcess;

    public sealed record IngestProcessResult(int ExitCode, string StdOut, string StdErr);

    public GraphFusionIngestCliTransport(
        string cliPath,
        ILogger? log = null,
        TimeSpan? timeout = null,
        Func<string, string, CancellationToken, Task<IngestProcessResult>>? runProcess = null)
    {
        _cliPath = cliPath;
        _log = log;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _runProcess = runProcess ?? RunProcessAsync;
    }

    public async Task<IngestSummary?> ShipAsync(StarsIngestBatch batch, ulong sidecarIdBase = 0, CancellationToken ct = default)
    {
        var ingest = batch.ToIngestBatch(A2aIngestTransport.ExtractRepoId(batch.RepoNodeId), sidecarIdBase);
        var payload = JsonSerializer.Serialize(ingest, JsonOpts);
        var result = await _runProcess(_cliPath, payload, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IngestCliException($"graphfusion-ingest-cli exited {result.ExitCode}: {Truncate(result.StdErr)}");
        IngestSummary summary;
        try
        {
            summary = JsonSerializer.Deserialize<IngestSummary>(result.StdOut, JsonOpts)
                ?? throw new IngestCliException("graphfusion-ingest-cli wrote an empty summary");
        }
        catch (JsonException jex)
        {
            throw new IngestCliException($"graphfusion-ingest-cli stdout is not a valid summary: {jex.Message}");
        }
        if (_log is not null)
            _log.LogInformation("gf-ingest-cli batch {BatchId}: ok={Ok} nodes={Nodes} local={Local} cross={Cross}",
                summary.BatchId, summary.Ok, summary.Nodes, summary.LocalEdges, summary.CrossBaseEdges);
        return summary;
    }

    private async Task<IngestProcessResult> RunProcessAsync(string path, string stdinPayload, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new IngestCliException($"failed to start {path}");
        await proc.StandardInput.WriteAsync(stdinPayload.AsMemory(), ct).ConfigureAwait(false);
        await proc.StandardInput.FlushAsync().ConfigureAwait(false);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var exitTask = proc.WaitForExitAsync(ct);
        var winner = await Task.WhenAny(exitTask, Task.Delay(_timeout, ct)).ConfigureAwait(false);
        if (winner != exitTask && !proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort reaping */ }
            throw new IngestCliException($"graphfusion-ingest-cli timed out after {_timeout.TotalSeconds:F0}s");
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new IngestProcessResult(proc.ExitCode, stdout, stderr);
    }

    private static string Truncate(string s) =>
        s.Length <= 300 ? s : s[..300] + "…";
}

/// <summary>Sidecar failure — service catches and degrades soft (skip the stage).</summary>
public sealed class IngestCliException(string message) : Exception(message);
