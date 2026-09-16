using Microsoft.Data.Sqlite;

namespace AgentFolio.Platform.Tenancy;

public sealed record TenantRecord(string TenantId, string Slug, long CreatedAt, bool Pinned);

/// <summary>
/// Global tenancy store (spec 002 NFR2a): tenants + user identity mapping live in
/// global.db; tenant data lives in per-tenant shards (see TenantShardRegistry).
/// Raw ADO over Microsoft.Data.Sqlite.Core, same pattern as SqliteStore.
/// </summary>
public sealed class TenantRegistry
{
    private readonly string _connectionString;

    public TenantRegistry(string globalDbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = globalDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>Path of the global db file (parent dir for tenant shard dirs).</summary>
    public string GlobalDbPath => new SqliteConnectionStringBuilder(_connectionString).DataSource;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
CREATE TABLE IF NOT EXISTS tenants (
    tenant_id TEXT PRIMARY KEY,
    slug TEXT NOT NULL UNIQUE,
    created_at INTEGER NOT NULL,
    pinned INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS users (
    github_id INTEGER PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id)
);
""";
        await cmd.ExecuteNonQueryAsync(ct);

        // s5-05 migration: share password hash column (NULL = ungated share).
        await using var shareCol = conn.CreateCommand();
        shareCol.CommandText = "ALTER TABLE tenants ADD COLUMN share_pw_hash TEXT NULL;";
        try
        {
            await shareCol.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException)
        {
            // Column already exists (ALTER TABLE has no IF NOT EXISTS in SQLite).
        }
    }

    /// <summary>Share-gate password hash for a tenant (null = ungated).</summary>
    public async Task<string?> GetSharePasswordHashAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT share_pw_hash FROM tenants WHERE tenant_id = $id;";
        cmd.Parameters.AddWithValue("$id", tenantId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s && s.Length > 0 ? s : null;
    }

    /// <summary>Sets (or clears with null) the share-gate password hash for a tenant.</summary>
    public async Task SetSharePasswordHashAsync(string tenantId, string? encodedHash, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tenants SET share_pw_hash = $h WHERE tenant_id = $id;";
        cmd.Parameters.AddWithValue("$h", (object?)encodedHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Creates a tenant with a fresh ULID. Throws SqliteException on duplicate slug.</summary>
    public async Task<TenantRecord> CreateTenantAsync(string slug, CancellationToken ct = default)
    {
        var tenantId = Ulid.NewUlid();
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tenants (tenant_id, slug, created_at, pinned) VALUES ($id, $slug, $created, 0);";
        cmd.Parameters.AddWithValue("$id", tenantId);
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$created", createdAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return new TenantRecord(tenantId, slug, createdAt, Pinned: false);
    }

    public async Task<TenantRecord?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, slug, created_at, pinned FROM tenants WHERE slug = $slug;";
        cmd.Parameters.AddWithValue("$slug", slug);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new TenantRecord(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetInt64(3) != 0);
    }

    public async Task DeleteTenantAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var users = conn.CreateCommand())
        {
            users.Transaction = (SqliteTransaction)tx;
            users.CommandText = "DELETE FROM users WHERE tenant_id = $id;";
            users.Parameters.AddWithValue("$id", tenantId);
            await users.ExecuteNonQueryAsync(ct);
        }
        await using (var tenants = conn.CreateCommand())
        {
            tenants.Transaction = (SqliteTransaction)tx;
            tenants.CommandText = "DELETE FROM tenants WHERE tenant_id = $id;";
            tenants.Parameters.AddWithValue("$id", tenantId);
            await tenants.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Identity mapping: same GitHub id always maps to the same tenant. Provisions a
    /// tenant with slug github-{id} on first sight; idempotent afterwards.
    /// </summary>
    public async Task<TenantRecord> GetOrCreateUserTenantAsync(long githubId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var lookup = conn.CreateCommand())
        {
            lookup.CommandText = """
SELECT t.tenant_id, t.slug, t.created_at, t.pinned FROM users u
JOIN tenants t ON t.tenant_id = u.tenant_id WHERE u.github_id = $gid;
""";
            lookup.Parameters.AddWithValue("$gid", githubId);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new TenantRecord(reader.GetString(0), reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3) != 0);
            }
        }

        // Provision: tenant named github-{id}. Unique constraint guards a concurrent race.
        var tenantId = Ulid.NewUlid();
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var slug = $"github-{githubId}";
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var insertTenant = conn.CreateCommand())
        {
            insertTenant.Transaction = (SqliteTransaction)tx;
            insertTenant.CommandText = "INSERT INTO tenants (tenant_id, slug, created_at, pinned) VALUES ($id, $slug, $created, 0);";
            insertTenant.Parameters.AddWithValue("$id", tenantId);
            insertTenant.Parameters.AddWithValue("$slug", slug);
            insertTenant.Parameters.AddWithValue("$created", createdAt);
            await insertTenant.ExecuteNonQueryAsync(ct);
        }
        await using (var insertUser = conn.CreateCommand())
        {
            insertUser.Transaction = (SqliteTransaction)tx;
            insertUser.CommandText = "INSERT INTO users (github_id, tenant_id) VALUES ($gid, $id);";
            insertUser.Parameters.AddWithValue("$gid", githubId);
            insertUser.Parameters.AddWithValue("$id", tenantId);
            await insertUser.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new TenantRecord(tenantId, slug, createdAt, Pinned: false);
    }
}
