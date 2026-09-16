namespace AgentFolio.Platform.Tenancy;

/// <summary>
/// Opens per-tenant shard databases (tenants/{tid}/data.db under the global db's
/// parent dir) on demand, with an LRU cap of 64 open shards (spec 002 NFR2a/NFR7:
/// index open < 50ms p95 via warm LRU; single-writer per shard).
/// The tenantId reaching this class is ALWAYS the registry's ULID, never client input.
/// </summary>
public sealed class TenantShardRegistry : IDisposable
{
    public const int MaxOpenShards = 64;

    /// <summary>Shards currently tracked by the LRU (test/diagnostics surface).</summary>
    public int TrackedCount { get { lock (_lock) { return _open.Count; } } }

    private readonly string _shardRoot;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(SqliteStore Store, string TenantId)>> _open = new();
    private readonly LinkedList<(SqliteStore Store, string TenantId)> _lru = new();
    private bool _disposed;

    public TenantShardRegistry(string globalDbPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(globalDbPath));
        if (string.IsNullOrEmpty(parent)) parent = ".";
        _shardRoot = Path.Combine(parent, "tenants");
    }

    public string ShardRoot => _shardRoot;

    public string ShardPath(string tenantId)
    {
        // tenantId is a server-generated ULID; validate defensively anyway.
        if (!Ulid.IsValidFormat(tenantId))
            throw new ArgumentException("tenantId must be a 26-char ULID.", nameof(tenantId));
        return Path.Combine(_shardRoot, tenantId, "data.db");
    }

    public async Task<SqliteStore> GetOrCreateShardAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_open.TryGetValue(tenantId, out var existing))
            {
                // Move to MRU position.
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Store;
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var path = ShardPath(tenantId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var store = new SqliteStore(path);
        await store.EnsureSchemaAsync(ct);
        await store.OpenAsync(ct);

        EvictIfNeeded(tenantId);

        SqliteStore? winnerStore = null;
        lock (_lock)
        {
            // Another thread may have raced us; keep the winner, dispose ours outside the lock.
            if (_open.TryGetValue(tenantId, out var winner))
            {
                _lru.Remove(winner);
                _lru.AddFirst(winner);
                winnerStore = winner.Value.Store;
            }
            else
            {
                var node = new LinkedListNode<(SqliteStore Store, string TenantId)>((store, tenantId));
                _open[tenantId] = node;
                _lru.AddFirst(node);
            }
        }
        return winnerStore ?? store; // raced-out store holds no handle (conn-per-op)
    }

    private void EvictIfNeeded(string justCreatedTenantId)
    {
        lock (_lock)
        {
            while (_open.Count >= MaxOpenShards && _lru.Last is { } last)
            {
                _lru.Remove(last);
                _open.Remove(last.Value.TenantId);
                // connection-per-operation store: no handle to release
            }
        }
    }

    /// <summary>Closes and forgets one shard's store (LRU eviction / disposal path).</summary>
    public async Task EvictShardAsync(string tenantId)
    {
        SqliteStore? store = null;
        lock (_lock)
        {
            if (_open.Remove(tenantId, out var node))
            {
                _lru.Remove(node);
                store = node.Value.Store;
            }
        }
        await Task.CompletedTask; // no handle to release
    }



    public void DisposeAll()
    {
        lock (_lock)
        {
            // connection-per-operation stores: nothing to release, just forget
            _lru.Clear();
            _open.Clear();
            _disposed = true;
        }
    }

    public void Dispose() => DisposeAll();
}
