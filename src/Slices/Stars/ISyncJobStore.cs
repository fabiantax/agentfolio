namespace Slices.Stars;

/// <summary>
/// Durable job persistence (the queue's state lives here, so an API restart
/// can resume or fail jobs instead of leaving zombie `running` rows).
/// Implemented over the tenant-shard SQLite store.
/// </summary>
public interface ISyncJobStore
{
    Task InsertAsync(StarsSyncJob job, CancellationToken ct = default);
    Task<StarsSyncJob?> GetAsync(Guid jobId, CancellationToken ct = default);
    Task<StarsSyncJob?> GetLatestAsync(string tenantId, CancellationToken ct = default);
    Task UpdateAsync(StarsSyncJob job, CancellationToken ct = default);
    /// <summary>Startup sweep: any job left `running` from a previous process is marked failed.</summary>
    Task FailStaleRunningAsync(string reason, CancellationToken ct = default);
}
