namespace AgentFolio.Slices.Stars;

using AgentFolio.Contracts;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// StarsSyncResult is defined in this namespace

/// <summary>Orchestrates fetching GitHub stars and syncing them as events + projections.</summary>
public sealed class StarsSyncService
{
    private readonly IGitHubStarsClient _starsClient;
    private readonly IItemStore _itemStore;

    public StarsSyncService(IGitHubStarsClient starsClient, IItemStore itemStore)
    {
        _starsClient = starsClient;
        _itemStore = itemStore;
    }

    /// <summary>
    /// Fetches all starred repos, appends StarsSynced events, and upserts projections.
    /// Returns count of fetched, upserted, and unchanged repos.
    /// </summary>
    public async Task<StarsSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var starredDtos = await _starsClient.GetAllStarredAsync(ct).ConfigureAwait(false);

        // Canonicalize by RepoId (key for dedup)
        var grouped = starredDtos
            .GroupBy(dto => dto.RepoId)
            .ToDictionary(g => g.Key, g => g.First());

        var now = DateTimeOffset.UtcNow;

        var fetched = starredDtos.Count;
        var unchanged = 0;
        var upserted = 0;

        // Materialize existing projections once (avoids per-dto full scans and
        // the inner-loop `continue` bug that only skipped one row).
        var existingById = new Dictionary<long, StarredRepoRecord>();
        await foreach (var existing in _itemStore.QueryStarsAsync(ct))
        {
            existingById[existing.RepoId] = existing;
        }

        foreach (var dto in grouped.Values)
        {
            if (existingById.TryGetValue(dto.RepoId, out var existing)
                && existing.FullName == dto.Name
                && existing.Description == dto.Description
                && existing.StarredAt == DateTimeOffset.Parse(dto.StarredAtIso))
            {
                unchanged++;
                continue;
            }

            // Append event then upsert projection in one transaction
            await _itemStore.UpsertStarAsync(new StarredRepoRecord(
                dto.RepoId,
                dto.Name,
                dto.Description,
                SerializeTopics(dto.Topics),
                DateTimeOffset.Parse(dto.StarredAtIso)), ct);

            upserted++;
        }

        return new StarsSyncResult(fetched, upserted, unchanged);
    }

    private static string SerializeTopics(IReadOnlyList<string> topics)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(topics);
        return json;
    }
}
/// <summary>Result of a stars sync operation.</summary>
public sealed record StarsSyncResult(int Fetched, int Upserted, int Unchanged);
