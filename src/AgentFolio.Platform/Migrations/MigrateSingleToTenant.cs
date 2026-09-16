using System.Text;
using AgentFolio.Contracts;
using AgentFolio.Platform.Tenancy;
using Microsoft.Data.Sqlite;

namespace AgentFolio.Platform.Migrations;

/// <summary>Counts copied by the single-tenant → multi-tenant migration.</summary>
public sealed record MigrationResult(int Stars, int Saves, int Events, string TenantId);

/// <summary>
/// s5-05: one-shot migration of a legacy single-tenant agentfolio.db into the
/// 'showcase' tenant's shard. Stars/saves are upserted (idempotent by primary
/// key); events are appended only when the exact (type, payload) row is not
/// already present in the shard, so re-running the migration adds nothing.
/// The source db is opened read-only — never written.
/// </summary>
public static class MigrateSingleToTenant
{
    public const string ShowcaseSlug = "showcase";

    public static async Task<MigrationResult> Run(
        string sourceDbPath,
        TenantRegistry registry,
        TenantShardRegistry shardRegistry,
        CancellationToken ct = default)
    {
        await registry.EnsureSchemaAsync(ct);

        var showcase = await registry.GetBySlugAsync(ShowcaseSlug, ct)
            ?? await registry.CreateTenantAsync(ShowcaseSlug, ct);

        var shard = await shardRegistry.GetOrCreateShardAsync(showcase.TenantId, ct);
        await shard.EnsureSchemaAsync(ct);
        await shard.OpenAsync(ct);

        var sourceConnString = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        int stars = 0, saves = 0, events = 0;

        await using (var src = new SqliteConnection(sourceConnString))
        {
            await src.OpenAsync(ct);

            await using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = "SELECT RepoId, FullName, Description, TopicsJson, StarredAt FROM stars;";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var repo = new StarredRepoRecord(
                        r.GetInt64(0), r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3),
                        DateTimeOffset.Parse(r.GetString(4)));
                    if (!await StarMatchesAsync(shardRegistry.ShardPath(showcase.TenantId), repo, ct))
                    {
                        await shard.UpsertStarAsync(repo, ct);
                        stars++;
                    }
                }
            }

            await using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = "SELECT CanonicalUrl, Title, TextExcerpt, IsStale, SavedAt FROM saves;";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var post = new SavedPostRecord(
                        r.GetString(0),
                        r.IsDBNull(1) ? null : r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2),
                        !r.IsDBNull(3) && r.GetInt64(3) != 0,
                        DateTimeOffset.Parse(r.GetString(4)));
                    if (!await SaveMatchesAsync(shardRegistry.ShardPath(showcase.TenantId), post, ct))
                    {
                        await shard.UpsertSaveAsync(post, ct);
                        saves++;
                    }
                }
            }

            await using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = "SELECT type, payload, occurred_at FROM events ORDER BY id ASC;";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var type = r.GetString(0);
                    var payload = r.GetString(1);
                    if (await EventAlreadyCopiedAsync(shardRegistry, showcase.TenantId, type, payload, ct))
                        continue;
                    await shard.AppendAsync(
                        new AppendOnlyEvent(type, payload, DateTimeOffset.Parse(r.GetString(2))), ct);
                    events++;
                }
            }
        }

        return new MigrationResult(stars, saves, events, showcase.TenantId);
    }

    /// <summary>Idempotency guard: exact (type, payload) event already in the shard.</summary>

    /// <summary>True when the shard already holds this star row with identical content.</summary>
    private static async Task<bool> StarMatchesAsync(
        string shardPath, StarredRepoRecord repo, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = shardPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FullName, Description, TopicsJson, StarredAt FROM stars WHERE RepoId = $id;";
        cmd.Parameters.AddWithValue("$id", repo.RepoId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return false;
        return r.GetString(0) == repo.FullName
            && (r.IsDBNull(1) ? repo.Description is null : r.GetString(1) == repo.Description)
            && (r.IsDBNull(2) ? repo.TopicsJson is null : r.GetString(2) == repo.TopicsJson)
            && r.GetString(3) == repo.StarredAt.ToString("O");
    }

    /// <summary>True when the shard already holds this save row with identical content.</summary>
    private static async Task<bool> SaveMatchesAsync(
        string shardPath, SavedPostRecord post, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = shardPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, TextExcerpt, IsStale, SavedAt FROM saves WHERE CanonicalUrl = $u;";
        cmd.Parameters.AddWithValue("$u", post.CanonicalUrl);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return false;
        return (r.IsDBNull(0) ? post.Title is null : r.GetString(0) == post.Title)
            && (r.IsDBNull(1) ? post.TextExcerpt is null : r.GetString(1) == post.TextExcerpt)
            && r.GetInt32(2) == (post.IsStale ? 1 : 0)
            && r.GetString(3) == post.SavedAt.ToString("O");
    }

    private static async Task<bool> EventAlreadyCopiedAsync(
        TenantShardRegistry shardRegistry, string tenantId, string type, string payload, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = shardRegistry.ShardPath(tenantId),
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE type = $t AND payload = $p;";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$p", payload);
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }
}
