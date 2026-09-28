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
