using System.Runtime.InteropServices;
using AgentFolio.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AgentFolio.Platform;

/// <summary>
/// Opens agentfolio.db (WAL), loads the sqlite-vec vec0 extension and ensures schema.
/// Append event BEFORE projection update in one transaction (ADR-0005 event-lite sourcing).
/// </summary>
public sealed class SqliteStore : IEventStore, IItemStore
{
    /// <summary>
    /// Runtime path of the native vec0 extension. sqlite-vec ships a NuGet package
    /// (SQLitePCLRaw-compatible) with per-RID native assets: on linux-x64 the loader
    /// resolves "vec0" against runtimes/linux-x64/native/vec0.so extracted to the
    /// output directory. If the package is absent, we fall back to a known local
    /// install path (documented for dev environments only).
    /// </summary>
    private const string Vec0ExtensionName = "vec0";

    private readonly string _connectionString;
    private readonly string? _vec0PathOverride;

    public SqliteStore(string dbPath, string? vec0PathOverride = null)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        _vec0PathOverride = vec0PathOverride;
    }

    /// <summary>Exact vec0 load pattern: EnableExtensions + LoadExtension + Load. WAL on first.</summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // 1) WAL journal mode (persistent per-database)
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = (string?)await wal.ExecuteScalarAsync(ct);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"WAL could not be enabled (journal_mode={mode}).");
        }

        // 2) sqlite-vec vec0 extension
        conn.EnableExtensions();
        var path = _vec0PathOverride ?? ResolveVec0Path();
        try
        {
            conn.LoadExtension(path);
        }
        catch (DllNotFoundException)
        {
            // native vec0 lib absent in this environment; vec0 features degrade gracefully.
            // Callers that need vec0 should use TryLoadVec0 and check the result.
            return;
        }

        // verify the extension actually registered
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pragma_module_list() WHERE name='vec0';";
            var ok = await check.ExecuteScalarAsync(ct);
            if (ok is not (long or int))
                throw new InvalidOperationException("vec0 extension loaded but module not registered.");
        }
    }

    /// <summary>Best-effort vec0 load. Returns false if native lib is missing (skip-guard signal for tests).</summary>
    public bool TryLoadVec0(out string? error)
    {
        error = null;
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            conn.EnableExtensions();
            conn.LoadExtension(_vec0PathOverride ?? ResolveVec0Path());
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or SqliteException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ResolveVec0Path()
    {
        // Normal resolution: "vec0" picks up runtimes/<rid>/native/vec0.so via the
        // SQLitePCLRaw native resolver. Windows: vec0.dll, macOS: vec0.dylib.
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vec0" : "vec0";
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        bool vec0Loaded;
        try
        {
            conn.EnableExtensions();
            conn.LoadExtension(_vec0PathOverride ?? ResolveVec0Path());
            vec0Loaded = true;
        }
        catch (DllNotFoundException)
        {
            vec0Loaded = false;
        }

        var sql = """
-- append-only event log (ADR-0005): no UPDATE/DELETE grants in code paths.
CREATE TABLE IF NOT EXISTS events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type TEXT NOT NULL,
    payload JSON NOT NULL DEFAULT '{}',
    occurred_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS stars (
    RepoId INTEGER NOT NULL PRIMARY KEY,
    FullName TEXT NOT NULL,
    Description TEXT,
    TopicsJson TEXT,
    StarredAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS stars_raw (
    RepoId INTEGER NOT NULL PRIMARY KEY,
    FullPayloadJson TEXT NOT NULL,
    ReadmeText TEXT,
    ReadmeFetchedAt TEXT,
    FetchedAt TEXT NOT NULL
);

-- #66 B5 sidecar id-book: dense node-id allocator + ingested marker. One row per
-- graph-ingested repo (NodeIdBase = repo node id; chunk i = NodeIdBase+1+i).
CREATE TABLE IF NOT EXISTS stars_graph_nodes (
    RepoId INTEGER NOT NULL PRIMARY KEY,
    NodeIdBase INTEGER NOT NULL,
    ChunkCount INTEGER NOT NULL DEFAULT 0,
    IngestedAt TEXT NOT NULL
);

-- #98 poison-repo quarantine: consecutive NER-stage failures per repo.
-- >=3 strikes -> the worker's selection skips the repo; a successful ingest
-- deletes the row (reset). One row per failing repo, not per attempt.
CREATE TABLE IF NOT EXISTS stars_ingest_failures (
    RepoId INTEGER NOT NULL PRIMARY KEY,
    ConsecutiveFailures INTEGER NOT NULL DEFAULT 1,
    LastFailureAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_stars_RepoId ON stars(RepoId);

CREATE TABLE IF NOT EXISTS saves (
    CanonicalUrl TEXT NOT NULL PRIMARY KEY,
    Title TEXT,
    TextExcerpt TEXT,
    IsStale INTEGER NOT NULL DEFAULT 0,
    SavedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_saves_CanonicalUrl ON saves(CanonicalUrl);

CREATE TABLE IF NOT EXISTS chat_messages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ConversationId TEXT NOT NULL DEFAULT '',
    Role TEXT NOT NULL DEFAULT 'user',
    Content TEXT NOT NULL,
    OccurredAt TEXT NOT NULL
);
""";

        if (vec0Loaded)
        {
            sql += """

CREATE VIRTUAL TABLE IF NOT EXISTS vec_stars USING vec0(
    embedding float[8]
);
""";
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendAsync(AppendOnlyEvent @event, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO events (type, payload, occurred_at) VALUES ($t, $p, $o);";
            cmd.Parameters.AddWithValue("$t", @event.Type);
            cmd.Parameters.AddWithValue("$p", @event.PayloadJson);
            cmd.Parameters.AddWithValue("$o", @event.OccurredAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async IAsyncEnumerable<StoredEvent> RebuildAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Phase 1: materialize the full event log with a short-lived read
            // (never hold the reader open across writes — SQLite single-writer).
            List<StoredEvent> events;
            using (var readConn = new SqliteConnection(_connectionString))
            {
                await readConn.OpenAsync(ct);
                await using var cmd = readConn.CreateCommand();
                cmd.CommandText = "SELECT id, type, payload, occurred_at FROM events ORDER BY id ASC;";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var list = new List<StoredEvent>();
                while (await reader.ReadAsync(ct))
                {
                    list.Add(new StoredEvent(reader.GetInt64(0), reader.GetString(1),
                        reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
                }
                events = list;
            }

            // Phase 2: truncate + replay in per-event transactions (same write path
            // as ingest — AppendThenProjectAsync owns its own connection/tx),
            // idempotent by construction: full truncate then deterministic replay.
            using (var conn = new SqliteConnection(_connectionString))
            {
                await conn.OpenAsync(ct);
                using (var truncate = conn.CreateCommand())
                {
                    truncate.CommandText = "DELETE FROM saves; DELETE FROM stars;";
                    await truncate.ExecuteNonQueryAsync(ct);
                }
            }

            foreach (var e in events)
            {
                if (e.Type == "star_synced")
                {
                    var repo = System.Text.Json.JsonSerializer.Deserialize<StarredRepoRecord>(e.PayloadJson);
                    if (repo != null)
                    {
                        await ProjectStarAsync(repo, ct);
                    }
                }
                else if (e.Type == "save_ingested")
                {
                    var post = System.Text.Json.JsonSerializer.Deserialize<SavedPostRecord>(e.PayloadJson);
                    if (post != null)
                    {
                        await ProjectSaveAsync(post, ct);
                    }
                }
                yield return e;
            }
        }

    // Projection-only writes (no event append) — used by RebuildAsync so replay
    // never grows the event log. Ingest paths use AppendThenProjectAsync instead.
    private async Task ProjectStarAsync(StarredRepoRecord repo, CancellationToken ct = default)
    {
        await using var db = NewDbContext();
        db.Database.ExecuteSqlRaw(
            "INSERT INTO stars (RepoId, FullName, Description, TopicsJson, StarredAt) VALUES ({0}, {1}, {2}, {3}, {4}) " +
            "ON CONFLICT(RepoId) DO UPDATE SET FullName=excluded.FullName, Description=excluded.Description, TopicsJson=excluded.TopicsJson, StarredAt=excluded.StarredAt;",
            repo.RepoId, repo.FullName, repo.Description, repo.TopicsJson, repo.StarredAt.ToString("O"));
    }

    private async Task ProjectSaveAsync(SavedPostRecord post, CancellationToken ct = default)
    {
        await using var db = NewDbContext();
        db.Database.ExecuteSqlRaw(
            "INSERT INTO saves (CanonicalUrl, Title, TextExcerpt, IsStale, SavedAt) VALUES ({0}, {1}, {2}, {3}, {4}) " +
            "ON CONFLICT(CanonicalUrl) DO UPDATE SET Title=excluded.Title, TextExcerpt=excluded.TextExcerpt, IsStale=excluded.IsStale, SavedAt=excluded.SavedAt;",
            post.CanonicalUrl, post.Title, post.TextExcerpt, post.IsStale ? 1 : 0, post.SavedAt.ToString("O"));
    }

    public async Task UpsertStarAsync(StarredRepoRecord repo, CancellationToken ct = default)
        => await AppendThenProjectAsync(
            "star_synced",
            System.Text.Json.JsonSerializer.Serialize(repo),
            (db) =>
            {
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO stars (RepoId, FullName, Description, TopicsJson, StarredAt) VALUES ({0}, {1}, {2}, {3}, {4}) " +
                    "ON CONFLICT(RepoId) DO UPDATE SET FullName=excluded.FullName, Description=excluded.Description, TopicsJson=excluded.TopicsJson, StarredAt=excluded.StarredAt;",
                    repo.RepoId, repo.FullName, repo.Description, repo.TopicsJson, repo.StarredAt.ToString("O"));
            },
            ct);

    // ---- stars_raw (#66 raw-first) ----

    public async Task UpsertStarRawAsync(long repoId, string fullPayloadJson, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO stars_raw (RepoId, FullPayloadJson, ReadmeText, ReadmeFetchedAt, FetchedAt)
            VALUES ($id, $json, NULL, NULL, $now)
            ON CONFLICT(RepoId) DO UPDATE SET
                FullPayloadJson=excluded.FullPayloadJson,
                FetchedAt=excluded.FetchedAt;
            """;
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.Parameters.AddWithValue("$json", fullPayloadJson);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStarReadmeAsync(long repoId, string readmeText, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE stars_raw SET ReadmeText=$t, ReadmeFetchedAt=$f WHERE RepoId=$id;";
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.Parameters.AddWithValue("$t", readmeText);
        cmd.Parameters.AddWithValue("$f", fetchedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<System.Collections.Generic.IReadOnlyList<long>> GetStarIdsMissingReadmeAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RepoId FROM stars_raw WHERE ReadmeText IS NULL ORDER BY FetchedAt LIMIT $lim;";
        cmd.Parameters.AddWithValue("$lim", limit);
        var result = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task<StarredRepoRaw?> GetStarRawAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RepoId, FullPayloadJson, ReadmeText, ReadmeFetchedAt, FetchedAt FROM stars_raw WHERE RepoId=$id;";
        cmd.Parameters.AddWithValue("$id", repoId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StarredRepoRaw(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)));
    }

    // ---- stars_graph_nodes (#66 B5 sidecar id-book) ----

    public async Task StarsGraphNodeBookedAsync(long repoId, long nodeIdBase, int chunkCount, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO stars_graph_nodes (RepoId, NodeIdBase, ChunkCount, IngestedAt)
            VALUES ($id, $base, $chunks, $now)
            ON CONFLICT(RepoId) DO UPDATE SET
                NodeIdBase=excluded.NodeIdBase, ChunkCount=excluded.ChunkCount, IngestedAt=excluded.IngestedAt;
            """;
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.Parameters.AddWithValue("$base", nodeIdBase);
        cmd.Parameters.AddWithValue("$chunks", chunkCount);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<System.Collections.Generic.ISet<long>> StarsGraphNodeBookedIdsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RepoId FROM stars_graph_nodes;";
        var result = new HashSet<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task<long> StarsGraphNodeNextIdAsync(long seedFloor, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(NodeIdBase + 1 + ChunkCount), 0) FROM stars_graph_nodes;";
        var maxEnd = await cmd.ExecuteScalarAsync(ct) switch
        {
            null or DBNull => 0L,
            long v => v,
            IConvertible c => c.ToInt64(System.Globalization.CultureInfo.InvariantCulture),
            _ => 0L,
        };
        return Math.Max(maxEnd, seedFloor);
    }

    // ---- stars_ingest_failures (#98 poison-repo quarantine) ----

    public async Task StarsGraphIngestFailedAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO stars_ingest_failures (RepoId, ConsecutiveFailures, LastFailureAt)
            VALUES ($id, 1, $now)
            ON CONFLICT(RepoId) DO UPDATE SET
                ConsecutiveFailures = ConsecutiveFailures + 1, LastFailureAt = excluded.LastFailureAt;
            """;
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<System.Collections.Generic.ISet<long>> StarsGraphIngestQuarantinedIdsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RepoId FROM stars_ingest_failures WHERE ConsecutiveFailures >= 3;";
        var result = new HashSet<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task StarsGraphIngestResetAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM stars_ingest_failures WHERE RepoId=$id;";
        cmd.Parameters.AddWithValue("$id", repoId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertSaveAsync(SavedPostRecord post, CancellationToken ct = default)
        => await AppendThenProjectAsync(
            "save_ingested",
            System.Text.Json.JsonSerializer.Serialize(post),
            (db) =>
            {
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO saves (CanonicalUrl, Title, TextExcerpt, IsStale, SavedAt) VALUES ({0}, {1}, {2}, {3}, {4}) " +
                    "ON CONFLICT(CanonicalUrl) DO UPDATE SET Title=excluded.Title, TextExcerpt=excluded.TextExcerpt, IsStale=excluded.IsStale, SavedAt=excluded.SavedAt;",
                    post.CanonicalUrl, post.Title, post.TextExcerpt, post.IsStale ? 1 : 0, post.SavedAt.ToString("O"));
            },
            ct);

    public async IAsyncEnumerable<StarredRepoRecord> QueryStarsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = NewDbContext();
        await foreach (var s in db.Stars.AsAsyncEnumerable().WithCancellation(ct))
            yield return new StarredRepoRecord(s.RepoId, s.FullName, s.Description, s.TopicsJson, s.StarredAt);
    }

    /// <summary>#123: enriched rows for the recall/rerank hay (stars_raw ⋈ stars).
    /// Rows with no projection match are skipped (name comes from the projection).</summary>
    public async Task<System.Collections.Generic.IReadOnlyList<StarsEnrichedRow>> GetStarsEnrichedAsync(
        CancellationToken ct = default)
    {
        var rows = new System.Collections.Generic.List<StarsEnrichedRow>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sr.RepoId, s.FullName, s.Description, s.TopicsJson, sr.UseFor, sr.ProblemsJson
            FROM stars_raw sr
            JOIN stars s ON s.RepoId = sr.RepoId;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StarsEnrichedRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return rows;
    }

    public async IAsyncEnumerable<SavedPostRecord> QuerySavesAsync(
        bool includeStale = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = NewDbContext();
        var q = db.Saves.AsQueryable();
        if (!includeStale) q = q.Where(s => !s.IsStale);
        await foreach (var s in q.AsAsyncEnumerable().WithCancellation(ct))
            yield return new SavedPostRecord(s.CanonicalUrl, s.Title, s.TextExcerpt, s.IsStale, s.SavedAt);
    }

    /// <summary>
    /// ADR-0005 ordering guarantee: the event row is INSERTed BEFORE the projection
    /// mutation, inside one transaction. If projection fails, the tx rolls back —
    /// the event never appears without its projection (and vice versa).
    /// </summary>
    private async Task AppendThenProjectAsync(
        string eventType, string payloadJson, Action<AgentFolioDbContext> project, CancellationToken ct)
    {
        await using var db = NewDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Events.Add(new StoredEventEntity { Type = eventType, Payload = payloadJson, OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        project(db);
        await tx.CommitAsync(ct);
    }

    private AgentFolioDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AgentFolioDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new AgentFolioDbContext(options);
    }
}
