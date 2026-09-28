namespace Slices.Stars;

/// <summary>
/// Default ISyncJobQueue: in-process Channel<T> hand-off + durable store rows.
/// The BackgroundService worker lives in StarsSyncWorker; this class is the
/// swappable seam — swap for a durable-workflow adapter without touching
/// endpoints or the domain service.
/// </summary>
public sealed class ChannelSyncJobQueue : ISyncJobQueue
{
    private readonly ISyncJobStore _store;
    private readonly System.Threading.Channels.Channel<StarsSyncJob> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<StarsSyncJob>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

    public ChannelSyncJobQueue(ISyncJobStore store) => _store = store;

    public async Task<StarsSyncJob> EnqueueAsync(string tenantId, bool force = false, CancellationToken ct = default)
    {
        var job = StarsSyncJob.Queued(tenantId, force);
        await _store.InsertAsync(job, ct).ConfigureAwait(false);
        await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
        return job;
    }

    public async Task<StarsSyncJob?> TryDequeueAsync(CancellationToken ct = default)
    {
        if (!_channel.Reader.TryRead(out var job))
            return null;
        job.MarkRunning();
        await _store.UpdateAsync(job, ct).ConfigureAwait(false);
        return job;
    }

    public async Task CompleteAsync(Guid jobId, int fetched, int upserted, int unchanged, CancellationToken ct = default)
    {
        var job = await _store.GetAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"sync job {jobId} not found");
        job.MarkCompleted(fetched, upserted, unchanged);
        await _store.UpdateAsync(job, ct).ConfigureAwait(false);
    }

    public async Task ReportProgressAsync(Guid jobId, int pagesDone, string note, CancellationToken ct = default)
    {
        var job = await _store.GetAsync(jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status != StarsSyncJobStatus.Running) return;
        job.ReportPageProgress(pagesDone, note);
        await _store.UpdateAsync(job, ct).ConfigureAwait(false);
    }

    public async Task FailAsync(Guid jobId, string error, CancellationToken ct = default)
    {
        var job = await _store.GetAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"sync job {jobId} not found");
        job.MarkFailed(error);
        await _store.UpdateAsync(job, ct).ConfigureAwait(false);
    }

    public Task<StarsSyncJob?> GetLatestAsync(string tenantId, CancellationToken ct = default)
        => _store.GetLatestAsync(tenantId, ct);

    public async Task<bool> IsBusyAsync(string tenantId, CancellationToken ct = default)
    {
        var latest = await _store.GetLatestAsync(tenantId, ct).ConfigureAwait(false);
        return latest is { Status: StarsSyncJobStatus.Queued or StarsSyncJobStatus.Running };
    }

}
