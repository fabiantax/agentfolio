using Slices.Stars;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>
/// The ISyncJobQueue CONTRACT (O4, #62): every implementation must pass these
/// assertions. Runs against the default ChannelSyncJobQueue (with the real
/// SQLite store on a temp file) AND an in-memory fake — swappability proven by
/// construction, not by promise.
/// </summary>
public class StarsSyncJobQueueContractTests
{
    private static string TempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "af-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "sync-jobs.db");
    }

    public static IEnumerable<object[]> Implementations()
    {
        yield return new object[] { "channel+sqlite", new Func<ISyncJobQueue>(() => new ChannelSyncJobQueue(new SqliteSyncJobStore(TempDb()))) };
        yield return new object[] { "in-memory fake", new Func<ISyncJobQueue>(() => new InMemorySyncJobQueueFake()) };
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task Enqueue_then_GetLatest_returns_queued_job(string _, Func<ISyncJobQueue> make)
    {
        var q = make();
        var job = await q.EnqueueAsync("t1");
        Assert.Equal(StarsSyncJobStatus.Queued, job.Status);

        var latest = await q.GetLatestAsync("t1");
        Assert.NotNull(latest);
        Assert.Equal(job.JobId, latest!.JobId);
        Assert.Equal(StarsSyncJobStatus.Queued, latest.Status);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task Dequeue_marks_running_and_completes_with_counters(string _, Func<ISyncJobQueue> make)
    {
        var q = make();
        var job = await q.EnqueueAsync("t1");

        var dequeued = await q.TryDequeueAsync();
        Assert.NotNull(dequeued);
        Assert.Equal(job.JobId, dequeued!.JobId);
        Assert.Equal(StarsSyncJobStatus.Running, dequeued.Status);

        await q.CompleteAsync(job.JobId, fetched: 1969, upserted: 1969, unchanged: 0);
        var latest = await q.GetLatestAsync("t1");
        Assert.Equal(StarsSyncJobStatus.Completed, latest!.Status);
        Assert.Equal(1969, latest.Fetched);
        Assert.Equal(1969, latest.Upserted);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task Fail_from_queued_or_running_is_legal_and_records_error(string _, Func<ISyncJobQueue> make)
    {
        var q = make();
        var job = await q.EnqueueAsync("t1");
        await q.FailAsync(job.JobId, "boom");
        var latest = await q.GetLatestAsync("t1");
        Assert.Equal(StarsSyncJobStatus.Failed, latest!.Status);
        Assert.Equal("boom", latest.Error);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task IsBusy_true_while_queued_or_running_false_after_terminal(string _, Func<ISyncJobQueue> make)
    {
        var q = make();
        var job = await q.EnqueueAsync("t1");
        Assert.True(await q.IsBusyAsync("t1"));
        await q.TryDequeueAsync();
        Assert.True(await q.IsBusyAsync("t1"));
        await q.CompleteAsync(job.JobId, 1, 1, 0);
        Assert.False(await q.IsBusyAsync("t1"));
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task Illegal_transition_throws(string _, Func<ISyncJobQueue> make)
    {
        var q = make();
        var job = await q.EnqueueAsync("t1");
        // queued -> completed is illegal (must be dequeued/running first)
        await Assert.ThrowsAsync<InvalidOperationException>(() => q.CompleteAsync(job.JobId, 1, 1, 0));
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task GetLatest_is_per_tenant(string _, Func<ISyncJobQueue> make)
    {
        var q = make();
        await q.EnqueueAsync("t1");
        Assert.Null(await q.GetLatestAsync("other-tenant"));
    }
}

/// <summary>Throwaway in-memory implementation — exists ONLY to prove the
/// contract is implementable without the SQLite/Channel machinery (DIP proof).</summary>
public sealed class InMemorySyncJobQueueFake : ISyncJobQueue
{
    private readonly Dictionary<Guid, StarsSyncJob> _jobs = new();
    private readonly Queue<Guid> _order = new();
    private readonly object _lock = new();

    public Task<StarsSyncJob> EnqueueAsync(string tenantId, bool force = false, CancellationToken ct = default)
    {
        var job = StarsSyncJob.Queued(tenantId, force);
        lock (_lock) { _jobs[job.JobId] = job; _order.Enqueue(job.JobId); }
        return Task.FromResult(job);
    }

    public Task<StarsSyncJob?> TryDequeueAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            while (_order.Count > 0)
            {
                var id = _order.Dequeue();
                var job = _jobs[id];
                if (job.Status == StarsSyncJobStatus.Queued)
                {
                    job.MarkRunning();
                    return Task.FromResult<StarsSyncJob?>(job);
                }
            }
        }
        return Task.FromResult<StarsSyncJob?>(null);
    }

    public Task CompleteAsync(Guid jobId, int fetched, int upserted, int unchanged, CancellationToken ct = default)
    { lock (_lock) { _jobs[jobId].MarkCompleted(fetched, upserted, unchanged); } return Task.CompletedTask; }

    public Task FailAsync(Guid jobId, string error, CancellationToken ct = default)
    { lock (_lock) { _jobs[jobId].MarkFailed(error); } return Task.CompletedTask; }

    public Task<StarsSyncJob?> GetLatestAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var latest = _jobs.Values.Where(j => j.TenantId == tenantId)
                .OrderByDescending(j => j.StartedAt).FirstOrDefault();
            return Task.FromResult(latest);
        }
    }

    public Task<bool> IsBusyAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs.Values.Any(j =>
                j.TenantId == tenantId && j.Status is StarsSyncJobStatus.Queued or StarsSyncJobStatus.Running));
        }
    }
}
