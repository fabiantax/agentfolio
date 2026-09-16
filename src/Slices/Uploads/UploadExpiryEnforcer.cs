using AgentFolio.Platform.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Slices.Uploads;

/// <summary>
/// v1.1 lifecycle enforcer (spec 002 s3-03). Scans a tracked-prefixes registry
/// every 15 minutes and deletes prefixes older than 24h via the object store.
///
/// v1.1 LIMITATION (documented): the registry is in-memory, keyed by
/// (key, uploadedAt). Tracked prefixes do NOT survive process restarts, so
/// objects uploaded before a restart are only reaped if re-tracked; a durable
/// manifest (e.g. S3 inventory or a manifest object) is deferred to v1.2.
/// </summary>
public sealed class UploadExpiryEnforcer : BackgroundService
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Retention = UploadService.Retention;

    private static readonly object RegistryLock = new();
    private static readonly Dictionary<string, DateTimeOffset> Tracked = new(StringComparer.Ordinal);

    private readonly IObjectStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<UploadExpiryEnforcer>? _logger;

    public UploadExpiryEnforcer(IObjectStore store, TimeProvider? clock = null, ILogger<UploadExpiryEnforcer>? logger = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Registers an uploaded object key with its upload time (in-memory, v1.1).</summary>
    public static void Track(string key, DateTimeOffset uploadedAt)
    {
        lock (RegistryLock)
        {
            Tracked[key] = uploadedAt;
        }
    }

    /// <summary>Test/diagnostics surface: current tracked key count.</summary>
    public static int TrackedCount
    {
        get { lock (RegistryLock) { return Tracked.Count; } }
    }

    public static void ClearRegistry()
    {
        lock (RegistryLock)
        {
            Tracked.Clear();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(_clock.GetUtcNow(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Upload expiry sweep failed; retrying next interval.");
            }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    /// <summary>Deletes prefixes whose newest upload is older than retention. Test-callable.</summary>
    public async Task<IReadOnlyList<string>> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        List<(string Prefix, DateTimeOffset UploadedAt)> due;
        lock (RegistryLock)
        {
            // Group object keys by their session prefix (tenants/{tid}/sessions/{sid}/).
            var newest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var (key, uploadedAt) in Tracked)
            {
                var prefix = SessionPrefixOf(key);
                if (prefix is null) continue;
                if (!newest.TryGetValue(prefix, out var t) || uploadedAt > t)
                    newest[prefix] = uploadedAt;
            }
            due = newest.Where(kv => now - kv.Value > Retention).Select(kv => (kv.Key, kv.Value)).ToList();
        }

        var reaped = new List<string>();
        foreach (var (prefix, _) in due)
        {
            if (await _store.DeletePrefixAsync(prefix, ct))
            {
                reaped.Add(prefix);
                lock (RegistryLock)
                {
                    foreach (var k in Tracked.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    {
                        Tracked.Remove(k);
                    }
                }
            }
        }
        return reaped;
    }

    internal static string? SessionPrefixOf(string key)
    {
        // tenants/{tid}/sessions/{sid}/{sha256}{ext}
        var marker = "/sessions/";
        var i = key.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = key[(i + marker.Length)..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        return key[..(i + marker.Length + slash + 1)];
    }
}
