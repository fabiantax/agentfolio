namespace Slices.Stars;

/// <summary>
/// The processing seam for stars-sync jobs (DIP): endpoints and the domain
/// service depend on THIS interface only. Implementations decide WHERE and HOW
/// jobs execute — today an in-process Channel+BackgroundService adapter; later
/// a durable-workflow adapter (Obelisk) without touching any caller.
/// </summary>
public interface ISyncJobQueue
{
    /// <summary>Enqueue a new sync job for the tenant. Returns the queued job.</summary>
    Task<StarsSyncJob> EnqueueAsync(string tenantId, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Try to dequeue the next queued job (marks it Running). Returns null when
    /// nothing is queued. Implementations must be safe for one concurrent worker.
    /// </summary>
    Task<StarsSyncJob?> TryDequeueAsync(CancellationToken ct = default);

    /// <summary>Persist a completed outcome.</summary>
    Task CompleteAsync(Guid jobId, int fetched, int upserted, int unchanged, CancellationToken ct = default);

    /// <summary>Persist a failure (from either the queued or running state).</summary>
    Task FailAsync(Guid jobId, string error, CancellationToken ct = default);

    /// <summary>Latest job for the tenant (any status), or null.</summary>
    Task<StarsSyncJob?> GetLatestAsync(string tenantId, CancellationToken ct = default);

    /// <summary>True when the tenant has a job in Queued or Running state.</summary>
    Task<bool> IsBusyAsync(string tenantId, CancellationToken ct = default);
}
