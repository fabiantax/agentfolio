namespace Slices.Stars;

using AgentFolio.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The worker half of the default queue adapter: dequeues jobs from
/// ISyncJobQueue and executes them against StarsSyncService. DI-registered as
/// a hosted service. Cancellation is the APP-lifetime token — a client
/// disconnect can never reach this (#62's entire reason to exist).
/// </summary>
public sealed class StarsSyncWorker : BackgroundService
{
    private readonly ISyncJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StarsSyncWorker> _logger;
    private StarsSyncJob? _current;

    private readonly ISyncJobStore _jobStore;

    public StarsSyncWorker(ISyncJobQueue queue, ISyncJobStore jobStore, IServiceScopeFactory scopeFactory,
        ILogger<StarsSyncWorker> logger)
    {
        _queue = queue;
        _jobStore = jobStore;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken appStopping)
    {
        // Restart sweep: any job left Queued/Running by a previous process is
        // marked failed — no zombie `running` rows after a crash/restart (#62 AC).
        try { await _jobStore.FailStaleRunningAsync("interrupted by API restart", CancellationToken.None).ConfigureAwait(false); }
        catch { /* a failed sweep must not kill the worker */ }

        while (!appStopping.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.TryDequeueAsync(appStopping).ConfigureAwait(false);
                if (job is null)
                {
                    await Task.Delay(500, appStopping).ConfigureAwait(false);
                    continue;
                }

                _current = job;
                _logger.LogInformation("stars sync job {JobId} running for tenant {TenantId}", job.JobId, job.TenantId);
                using var scope = _scopeFactory.CreateScope();
                var starsClient = scope.ServiceProvider.GetRequiredService<IGitHubStarsClient>();
                var shardRegistry = scope.ServiceProvider.GetRequiredService<TenantShardRegistry>();
                var store = await shardRegistry.GetOrCreateShardAsync(job.TenantId, appStopping).ConfigureAwait(false);
                var objectStore = scope.ServiceProvider.GetService<AgentFolio.Platform.Storage.IObjectStore>();

                var service = new StarsSyncService(starsClient, store);
                var snapshotKey = StarsSnapshot.KeyFor(job.TenantId);
                StarsSyncResult result;
                void ReportProgress(int pages, string note)
                {
                    _ = _queue.ReportProgressAsync(job.JobId, pages, note, CancellationToken.None)
                        .ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }

                if (objectStore is not null)
                {
                    result = await service.SyncAsync(appStopping, async (repos, token) =>
                    {
                        var payload = new StarsSnapshot(DateTimeOffset.UtcNow, repos).Serialize();
                        await objectStore.PutAsync(snapshotKey,
                            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload)),
                            "application/json", token).ConfigureAwait(false);
                    }, ReportProgress).ConfigureAwait(false);
                }
                else
                {
                    result = await service.SyncAsync(appStopping, null, ReportProgress).ConfigureAwait(false);
                }

                await _queue.CompleteAsync(job.JobId, result.Fetched, result.Upserted, result.Unchanged,
                    CancellationToken.None).ConfigureAwait(false);
                _current = null;

                // README backfill rides every successful sync (#66 A2): one bounded, paced
                // batch per sync. With ~1,969 repos and batchSize 50, full coverage takes
                // ~40 syncs — or the next force-sync run. Restart-safe: cursor = store state.
                try
                {
                    var backfill = new StarsReadmeBackfillService(starsClient, store,
                        onProgress: (n, note) => { /* logged below via counter; progress fields already closed */ });
                    var (rFetched, rMissing, rSkipped) = await backfill.RunBatchAsync(appStopping).ConfigureAwait(false);
                    _logger.LogInformation("readme backfill batch: {Fetched} fetched, {Missing} without readme, {Skipped} skipped",
                        rFetched, rMissing, rSkipped);
                }
                catch (OperationCanceledException) when (appStopping.IsCancellationRequested) { throw; }
                catch (Exception bex)
                {
                    _logger.LogWarning(bex, "readme backfill batch failed (non-fatal; retries next sync)");
                }

                // NER -> graph ingestion (#66 B5 wiring): chunk + extract entities/relations
                // from the READMEs that now exist, upsert into GraphFusion. All services
                // optional; absence = skip (the graph lags, never breaks).
                try
                {
                    var gfClient = scope.ServiceProvider.GetService<Graph.GraphFusionClient>();
                    var ner = scope.ServiceProvider.GetService<Extraction.GlinerBatchClient>();
                    if (gfClient is not null && ner is not null && objectStore is not null)
                    {
                        var repoNames = new System.Collections.Generic.Dictionary<long, string>();
                        await foreach (var s in store.QueryStarsAsync(appStopping).ConfigureAwait(false))
                            repoNames[s.RepoId] = s.FullName;

                        var transport = Graph.StarsIngestTransportRouting.Select(gfClient);
                        var ingest = new Graph.StarsGraphIngestService(
                            transport, ner,
                            scope.ServiceProvider.GetService<Extraction.IRelationExtractor>());
                        // Bounded slice per sync: repos with a readme NOT yet graph-ingested,
                        // up to 20/run — the sidecar id-book doubles as the ingest cursor
                        // (booked = already in the graph), so the slice advances every fire.
                        // Dense-id allocation: GF sizes CSR/adjacency/embedding tables at
                        // O(max node id), so node ids must stay contiguous across batches.
                        // Repo node = base, chunk i = base+1+i, spans allocated from
                        // stars_graph_nodes; seed floor = GF_INGEST_ID_SEED (the existing
                        // store's max node id). Fail-loud when the sidecar is active and the
                        // seed is unset: a 0 seed would collide with pre-existing nodes.
                        var idSeed = 0L;
                        if (transport is Graph.GraphFusionIngestCliTransport)
                        {
                            var seedRaw = Environment.GetEnvironmentVariable("GF_INGEST_ID_SEED");
                            if (string.IsNullOrWhiteSpace(seedRaw))
                                throw new InvalidOperationException(
                                    "GF_INGEST_ID_SEED is required with the sidecar ingest transport — " +
                                    "set it to the graph store's current max node id (a 0 seed would collide).");
                            idSeed = long.Parse(seedRaw, System.Globalization.CultureInfo.InvariantCulture);
                        }
                        var booked = await store.StarsGraphNodeBookedIdsAsync(appStopping).ConfigureAwait(false);
                        var nextId = await store.StarsGraphNodeNextIdAsync(idSeed, appStopping).ConfigureAwait(false);

                        var total = new Graph.StarsGraphIngestService.RunStats(0, 0, 0, 0, 0);
                        var processed = 0;
                        var allIds = new System.Collections.Generic.List<long>();
                        await foreach (var s in store.QueryStarsAsync(appStopping).ConfigureAwait(false))
                            allIds.Add(s.RepoId);
                        var missing = new System.Collections.Generic.HashSet<long>(
                            await store.GetStarIdsMissingReadmeAsync(int.MaxValue, appStopping).ConfigureAwait(false));
                        foreach (var rid in allIds.Where(id => !missing.Contains(id) && !booked.Contains(id)).Take(20))
                        {
                            if (appStopping.IsCancellationRequested) break;
                            var raw = await store.GetStarRawAsync(rid, appStopping).ConfigureAwait(false);
                            if (raw?.ReadmeText is not { Length: > 0 }) continue;
                            var fullName = repoNames.TryGetValue(rid, out var fn) ? fn : $"repo:{rid}";
                            var st = await ingest.IngestRepoAsync(rid, fullName, null, null, raw.ReadmeText,
                                repoNames, Convert.ToUInt64(nextId), appStopping).ConfigureAwait(false);
                            if (st is null)
                            {
                                _logger.LogWarning("NER graph ingest: transport failed for {RepoId} — stage skipped this run", rid);
                                continue;
                            }
                            // Book the ACTUAL span the batch used; next repo continues
                            // contiguously. A failed booking self-heals: batch ids are
                            // deterministic (stars-repo:N), so a re-run replays the sidecar
                            // ledger and books again.
                            await store.StarsGraphNodeBookedAsync(rid, nextId, st.Chunks, appStopping).ConfigureAwait(false);
                            nextId += 1 + st.Chunks;
                            total = new Graph.StarsGraphIngestService.RunStats(
                                total.Repos + st.Repos, total.Chunks + st.Chunks, total.Entities + st.Entities,
                                total.Relations + st.Relations, total.MentionEdges + st.MentionEdges);
                            processed++;
                        }
                        _logger.LogInformation(
                            "NER graph ingest: {Processed} repos, {Chunks} chunks, {Entities} entities, {Relations} relations, {Edges} mention edges (next node id {NextId})",
                            processed, total.Chunks, total.Entities, total.Relations, total.MentionEdges, nextId);
                    }
                }
                catch (OperationCanceledException) when (appStopping.IsCancellationRequested) { throw; }
                catch (Exception nex)
                {
                    _logger.LogWarning(nex, "NER graph ingest failed (non-fatal; retries next sync)");
                }
                _logger.LogInformation("stars sync job {JobId} completed: {Fetched} fetched, {Upserted} upserted, {Unchanged} unchanged",
                    job.JobId, result.Fetched, result.Upserted, result.Unchanged);
            }
            catch (OperationCanceledException) when (appStopping.IsCancellationRequested)
            {
                break; // app shutdown — not a job failure
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "stars sync job failed");
                if (_current is not null)
                {
                    try { await _queue.FailAsync(_current.JobId, $"sync_failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false); }
                    catch { /* never let the worker die */ }
                    _current = null;
                }
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

}
