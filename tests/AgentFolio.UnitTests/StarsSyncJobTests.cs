using Slices.Stars;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>Job-entity lifecycle: only legal transitions survive (DDD invariants).</summary>
public class StarsSyncJobTests
{
    [Fact]
    public void Queued_to_running_to_completed_is_the_happy_path()
    {
        var job = StarsSyncJob.Queued("t1");
        Assert.Equal(StarsSyncJobStatus.Queued, job.Status);

        job.MarkRunning();
        Assert.Equal(StarsSyncJobStatus.Running, job.Status);

        job.MarkCompleted(fetched: 10, upserted: 8, unchanged: 2);
        Assert.Equal(StarsSyncJobStatus.Completed, job.Status);
        Assert.Equal(10, job.Fetched);
        Assert.Equal(8, job.Upserted);
        Assert.Equal(2, job.Unchanged);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.Error);
    }

    [Fact]
    public void Completed_job_cannot_go_back_to_running()
    {
        var job = StarsSyncJob.Queued("t1");
        job.MarkRunning();
        job.MarkCompleted(1, 1, 0);
        Assert.Throws<InvalidOperationException>(job.MarkRunning);
    }

    [Fact]
    public void Failed_job_cannot_complete()
    {
        var job = StarsSyncJob.Queued("t1");
        job.MarkFailed("boom");
        Assert.Throws<InvalidOperationException>(() => job.MarkCompleted(1, 1, 0));
        Assert.Equal("boom", job.Error);
    }

    [Fact]
    public void Queued_job_cannot_complete_without_running()
    {
        var job = StarsSyncJob.Queued("t1");
        Assert.Throws<InvalidOperationException>(() => job.MarkCompleted(1, 1, 0));
    }

    [Fact]
    public void Failed_can_come_from_queued_or_running()
    {
        var q = StarsSyncJob.Queued("t1");
        q.MarkFailed("never started");
        Assert.Equal(StarsSyncJobStatus.Failed, q.Status);

        var r = StarsSyncJob.Queued("t2");
        r.MarkRunning();
        r.MarkFailed("died mid-crawl");
        Assert.Equal(StarsSyncJobStatus.Failed, r.Status);
    }
}
