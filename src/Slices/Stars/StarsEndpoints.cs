namespace Slices.Stars;

using AgentFolio.Contracts;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        app.MapPost("/stars/sync", async (HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            IGitHubStarsClient starsClient;
            try
            {
                starsClient = sp.GetRequiredService<IGitHubStarsClient>();
            }
            catch (InvalidOperationException)
            {
                // AGENTS.md rule 1: no silent fallback — name the missing dependency.
                return Results.Json(new { error = "github_token_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var tid = await ResolveStarTenantIdAsync(http, ct);
            if (tid is null)
            {
                return Results.Json(new { error = "tenancy_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var shards = sp.GetRequiredService<TenantShardRegistry>();
            var store = await shards.GetOrCreateShardAsync(tid, ct);

            var service = new StarsSyncService(starsClient, store);
            var result = await service.SyncAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        // GET /stars?cursor=&limit= listing from IItemStore ordered by StarredAt desc
        app.MapGet("/stars", async (HttpContext http, IServiceProvider sp, string? cursor, int limit = 20, CancellationToken ct = default) =>
        {
            var tid = await ResolveStarTenantIdAsync(http, ct);
            if (tid is null)
            {
                return Results.Json(new { error = "tenancy_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var shards = sp.GetRequiredService<TenantShardRegistry>();
            var store = await shards.GetOrCreateShardAsync(tid, ct);

            var all = new List<AgentFolio.Contracts.StarredRepoRecord>();
            await foreach (var s in store.QueryStarsAsync(ct))
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

            return Results.Ok(page.Select(s => new
            {
                s.RepoId,
                s.FullName,
                s.Description,
                s.StarredAt
            }));
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Tenant boundary is server-derived (AGENTS.md rule 4): tid claim first,
    /// showcase fallback — a broken registry never 500s.
    /// </summary>
    private static async Task<string?> ResolveStarTenantIdAsync(HttpContext http, CancellationToken ct)
    {
        var tid = http.User.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(tid))
        {
            try
            {
                var registry = http.RequestServices.GetService<TenantRegistry>();
                tid = registry is null ? null : (await registry.GetBySlugAsync("showcase", ct))?.TenantId;
            }
            catch
            {
                tid = null;
            }
        }

        return string.IsNullOrEmpty(tid) ? null : tid;
    }
}
