namespace Slices.Stars;

using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed sync-job store over the TENANT SHARD database (same file as
/// the stars projection — the job is part of the Stars bounded context).
/// Schema created lazily; restart sweep marks zombie running rows failed.
/// </summary>
public sealed class SqliteSyncJobStore : ISyncJobStore
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public SqliteSyncJobStore(string shardDbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = shardDbPath }.ToString();
        EnsureSchema();
    }

    // Synchronous schema create on a local SQLite file is instantaneous; called
    // once from the ctor (same pattern as TenantRegistry.EnsureSchemaAsync's
    // bootstrap path, minus the needless async).
    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_jobs (
                JobId TEXT PRIMARY KEY,
                TenantId TEXT NOT NULL,
                Status TEXT NOT NULL,
                Fetched INTEGER NOT NULL DEFAULT 0,
                Upserted INTEGER NOT NULL DEFAULT 0,
                Unchanged INTEGER NOT NULL DEFAULT 0,
                Error TEXT,
                Force INTEGER NOT NULL DEFAULT 0,
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_sync_jobs_tenant ON sync_jobs(TenantId, StartedAt);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task InsertAsync(StarsSyncJob job, CancellationToken ct = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_jobs (JobId, TenantId, Status, Fetched, Upserted, Unchanged, Error, Force, StartedAt, FinishedAt) VALUES ($j,$t,$s,$f,$u,$u2,$e,$fo,$st,$fi);";
        cmd.Parameters.AddWithValue("$j", job.JobId.ToString());
        cmd.Parameters.AddWithValue("$t", job.TenantId);
        cmd.Parameters.AddWithValue("$s", job.Status.ToString());
        cmd.Parameters.AddWithValue("$f", job.Fetched);
        cmd.Parameters.AddWithValue("$u", job.Upserted);
        cmd.Parameters.AddWithValue("$u2", job.Unchanged);
        cmd.Parameters.AddWithValue("$e", (object?)job.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fo", job.Force ? 1 : 0);
        cmd.Parameters.AddWithValue("$st", job.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$fi", (object?)job.FinishedAt?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(StarsSyncJob job, CancellationToken ct = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE sync_jobs SET Status=$s, Fetched=$f, Upserted=$u, Unchanged=$u2, Error=$e, FinishedAt=$fi WHERE JobId=$j;";
        cmd.Parameters.AddWithValue("$j", job.JobId.ToString());
        cmd.Parameters.AddWithValue("$s", job.Status.ToString());
        cmd.Parameters.AddWithValue("$f", job.Fetched);
        cmd.Parameters.AddWithValue("$u", job.Upserted);
        cmd.Parameters.AddWithValue("$u2", job.Unchanged);
        cmd.Parameters.AddWithValue("$e", (object?)job.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fi", (object?)job.FinishedAt?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StarsSyncJob?> GetAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT JobId, TenantId, Status, Fetched, Upserted, Unchanged, Error, Force, StartedAt, FinishedAt FROM sync_jobs WHERE JobId=$j;";
        cmd.Parameters.AddWithValue("$j", jobId.ToString());
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<StarsSyncJob?> GetLatestAsync(string tenantId, CancellationToken ct = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT JobId, TenantId, Status, Fetched, Upserted, Unchanged, Error, Force, StartedAt, FinishedAt FROM sync_jobs WHERE TenantId=$t ORDER BY StartedAt DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task FailStaleRunningAsync(string reason, CancellationToken ct = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE sync_jobs SET Status='Failed', Error=$e, FinishedAt=$f WHERE Status IN ('Queued','Running');";
        cmd.Parameters.AddWithValue("$e", reason);
        cmd.Parameters.AddWithValue("$f", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<StarsSyncJob?> ReadOneAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StarsSyncJob(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<StarsSyncJobStatus>(reader.GetString(2)),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            reader.GetInt64(7) != 0);
    }
}
