namespace Slices.Stars;

using AgentFolio.Contracts;

/// <summary>
/// README backfill (#66 A2): cursor-driven, rate-safe (<=1 req/s), restart-safe.
/// One run = one bounded batch; the job machinery re-runs until no rows are missing.
/// </summary>
public sealed class StarsReadmeBackfillService(
    IGitHubStarsClient starsClient,
    IItemStore itemStore,
    Action<int, string>? onProgress = null,
    int batchSize = 50,
    int paceMilliseconds = 1100)
{
    public async Task<(int Fetched, int Missing, int Skipped)> RunBatchAsync(CancellationToken ct)
    {
        if (starsClient is not HttpGitHubStarsClient http)
            throw new InvalidOperationException("readme backfill requires the HTTP stars client");
        if (itemStore is not AgentFolio.Platform.SqliteStore store)
            throw new InvalidOperationException("readme backfill requires the SQLite shard store");

        var ids = await store.GetStarIdsMissingReadmeAsync(batchSize, ct).ConfigureAwait(false);
        var fetched = 0; var missing = 0; var skipped = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await store.GetStarRawAsync(ids[i], ct).ConfigureAwait(false);
            if (raw is null) { skipped++; continue; }

            string? owner = null, name = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw.FullPayloadJson);
                owner = doc.RootElement.GetProperty("owner").GetProperty("login").GetString();
                name = doc.RootElement.GetProperty("name").GetString();
            }
            catch { skipped++; continue; }
            if (owner is null || name is null) { skipped++; continue; }

            try
            {
                var text = await http.GetReadmeAsync(owner, name, ct).ConfigureAwait(false);
                await store.SetStarReadmeAsync(ids[i], text ?? "", DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (text is null) missing++; else fetched++;
                onProgress?.Invoke(i + 1, $"readme {i + 1}/{ids.Count}: {owner}/{name} {(text is null ? "(none)" : $"{text.Length}ch")}");
            }
            catch (System.Net.Http.HttpRequestException)
            {
                // transient: leave row without README — next batch retries it
                skipped++;
            }

            if (i < ids.Count - 1)
                await Task.Delay(paceMilliseconds, ct).ConfigureAwait(false);
        }
        return (fetched, missing, skipped);
    }
}
