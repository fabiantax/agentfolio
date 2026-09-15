namespace AgentFolio.Slices.Stars;

using AgentFolio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Maps the stars sync and listing endpoints.</summary>
public static class StarsEndpoints
{
    /// <summary>POST /stars/sync - triggers GitHub stars sync.</summary>
    /// <remarks>
    /// Requires cookie auth (enforced by caller wiring). Just builds the route;
    /// authentication is handled by the global cookie middleware.
    /// </remarks>
    public static IEndpointRouteBuilder MapStarsApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/stars/sync", async (StarsSyncService service, CancellationToken ct) =>
        {
            var result = await service.SyncAsync(ct);
            return Results.Ok(result);
        });

        // GET /stars?cursor=&limit= listing from IItemStore ordered by StarredAt desc
        app.MapGet("/stars", async (IItemStore store, string cursor, int limit = 20) =>
        {
            var all = new List<AgentFolio.Contracts.StarredRepoRecord>();
            await foreach (var s in store.QueryStarsAsync())
                all.Add(s);

            // Order by StarredAt descending
            var ordered = all.OrderByDescending(s => s.StarredAt).ToList();

            // Cursor-based pagination: if cursor provided, filter by repo id < cursor
            if (!string.IsNullOrEmpty(cursor) && long.TryParse(cursor, out var cursorId))
            {
                ordered = ordered.Where(s => s.RepoId < cursorId).ToList();
            }

            // Apply limit
            var page = ordered.Take(limit).ToList();

            return page.Select(s => new
            {
                s.RepoId,
                s.FullName,
                s.Description,
                s.StarredAt
            });
        });

        return app;
    }
}