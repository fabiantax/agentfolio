namespace AgentFolio.Platform.Storage;

/// <summary>
/// Thread-safe in-memory IObjectStore for unit tests and local dev.
/// Prefix matching is a plain StartsWith on the key, mirroring S3 list semantics.
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _objects = new(StringComparer.Ordinal);

    public Task PutAsync(string key, Stream data, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        data.CopyTo(ms, 81920);
        var bytes = ms.ToArray();
        lock (_lock)
        {
            _objects[key] = (bytes, contentType);
        }
        return Task.CompletedTask;
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<Stream?>(_objects.TryGetValue(key, out var v)
                ? new MemoryStream(v.Bytes, writable: false)
                : null);
        }
    }

    public Task<bool> DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var victims = _objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in victims)
            {
                _objects.Remove(k);
            }
            return Task.FromResult(victims.Count > 0);
        }
    }

    public Task<long?> StatAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<long?>(_objects.TryGetValue(key, out var v) ? v.Bytes.LongLength : null);
        }
    }
}
