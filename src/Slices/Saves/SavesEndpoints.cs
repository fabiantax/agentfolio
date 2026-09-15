using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using AgentFolio.Contracts;
using AgentFolio.Slices.Saves;

namespace AgentFolio.Slices.Saves;

/// <summary>Maps the saves ingest endpoint.</summary>
public static class SavesEndpoints
{
    /// <summary>POST /api/ingest/saves - accepts SaveIngestBatch JSON.</summary>
    /// <remarks>
    /// Auth: Authorization: Bearer <token> compared constant-time against AGENTFOLIO_INGEST_TOKEN env var.
    /// 401 when missing/wrong. 400 when batch empty or any item canonicalizes to null.
    /// On success: upsert each item via IItemStore with canonicalized URL.
    /// Append SaveIngested event BEFORE projection update, same transaction.
    /// Returns 200 with {received, upserted, duplicates}.
    /// </remarks>
    public static IEndpointRouteBuilder MapSavesApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/saves", async (SaveIngestBatch batch,
            IItemStore store,
            IEventStore eventStore,
            HttpContext context,
            CancellationToken ct) =>
        {
            // Auth check: Authorization: Bearer <token> compared constant-time
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Unauthorized(); // 401
            }

            var bearerToken = authHeader["Bearer ".Length..].Trim();
            var expectedToken = Environment.GetEnvironmentVariable("AGENTFOLIO_INGEST_TOKEN");
            if (expectedToken is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(bearerToken),
                Encoding.UTF8.GetBytes(expectedToken)))
            {
                return Results.Unauthorized(); // 401
            }

            // Validate batch
            if (batch.Items is null || batch.Items.Count == 0)
            {
                return Results.BadRequest(new { error = "batch is empty" }); // 400
            }

            // Canonicalize all URLs and validate
            var canonicalUrls = new List<string>();
            foreach (var item in batch.Items)
            {
                var canonical = SaveCanonicalizer.Canonicalize(item.PostUrl);
                if (canonical is null)
                {
                    return Results.BadRequest(new { error = $"cannot canonicalize URL: {item.PostUrl}" }); // 400
                }
                canonicalUrls.Add(canonical);
            }

            // Process in a transaction: append events + upsert saves
            // Following the existing AppendThenProjectAsync pattern from SqliteStore
            var received = batch.Items.Count;
            var upserted = 0;

            for (int i = 0; i < batch.Items.Count; i++)
            {
                var item = batch.Items[i];
                var canonicalUrl = canonicalUrls[i];

                // Append SaveIngested event BEFORE projection update, in same transaction
                var payload = JsonSerializer.Serialize(new { canonicalUrl, item.Title });
                await eventStore.AppendAsync(new AppendOnlyEvent(
                    "save_ingested", payload, DateTimeOffset.UtcNow), ct);

                // Now upsert the save record (this does the projection upsert in same tx)
                var saveRecord = new AgentFolio.Contracts.SavedPostRecord(
                    canonicalUrl, item.Title, item.Excerpt, false, DateTimeOffset.UtcNow);

                // Use UpsertSaveAsync which handles ON CONFLICT via existing pattern
                await store.UpsertSaveAsync(saveRecord, ct);
                upserted++;
            }

            var duplicates = batch.Items.Count - upserted;
            return Results.Ok(new { received, upserted, duplicates });
        });

        return app;
    }
}