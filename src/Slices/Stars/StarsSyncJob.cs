namespace Slices.Stars;

/// <summary>
/// The sync job entity: lifecycle transitions are the ONLY legal moves and are
/// enforced here (DDD aggregate root for the stars-sync bounded context).
/// </summary>
public sealed class StarsSyncJob
{
    public Guid JobId { get; }
    public string TenantId { get; }
    public StarsSyncJobStatus Status { get; private set; }
    public int Fetched { get; internal set; }
    public int Upserted { get; internal set; }
    public int Unchanged { get; internal set; }
    public string? Error { get; private set; }
    public bool Force { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public StarsSyncJob(Guid jobId, string tenantId, StarsSyncJobStatus status,
        int fetched, int upserted, int unchanged, string? error,
        DateTimeOffset startedAt, DateTimeOffset? finishedAt, bool force = false)
    {
        JobId = jobId;
        TenantId = tenantId;
        Status = status;
        Fetched = fetched;
        Upserted = upserted;
        Unchanged = unchanged;
        Error = error;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        Force = force;
    }

    public static StarsSyncJob Queued(string tenantId, bool force = false) =>
        new(Guid.NewGuid(), tenantId, StarsSyncJobStatus.Queued, 0, 0, 0, null, DateTimeOffset.UtcNow, null, force);

    public void MarkRunning()
    {
        EnsureTransition(StarsSyncJobStatus.Queued);
        Status = StarsSyncJobStatus.Running;
    }

    public void MarkCompleted(int fetched, int upserted, int unchanged)
    {
        EnsureTransition(StarsSyncJobStatus.Running);
        Fetched = fetched; Upserted = upserted; Unchanged = unchanged;
        Status = StarsSyncJobStatus.Completed;
        FinishedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string error)
    {
        EnsureTransition(StarsSyncJobStatus.Queued, StarsSyncJobStatus.Running);
        Error = error;
        Status = StarsSyncJobStatus.Failed;
        FinishedAt = DateTimeOffset.UtcNow;
    }

    private void EnsureTransition(params StarsSyncJobStatus[] from)
    {
        if (!from.Contains(Status))
            throw new InvalidOperationException(
                $"illegal sync-job transition {Status} (allowed from: {string.Join(',', from)})");
    }
}

public enum StarsSyncJobStatus { Queued, Running, Completed, Failed }
