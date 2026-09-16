using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Slices.Tenancy;

/// <summary>
/// Admin tenancy API (spec 002 s1-01): create/get/delete tenants in global.db and
/// dispose per-tenant shard directories. Admin gate: X-Admin-Token == env
/// AGENTFOLIO_ADMIN_TOKEN. Cookie auth from Program.cs is untouched.
/// </summary>
public static class TenancyEndpoints
{
    public static IEndpointRouteBuilder MapTenancyApi(this IEndpointRouteBuilder app)
    {
        var tenants = app.MapGroup("/api/tenants");

        tenants.MapPost("", async (HttpContext http, TenantRegistry registry, TenantShardRegistry shards, CreateTenantRequest? request, CancellationToken ct) =>
        {
            if (!IsAdmin(http)) return Results.Json(new { error = "admin_token_required" }, statusCode: StatusCodes.Status401Unauthorized);
            if (request is null)
                return Results.Json(new { error = "invalid_slug", reason = "missing" }, statusCode: StatusCodes.Status400BadRequest);
            if (!SlugValidator.IsValid(request.Slug, out var reason))
                return Results.Json(new { error = "invalid_slug", reason }, statusCode: StatusCodes.Status400BadRequest);

            var slug = request.Slug;
            var existing = await registry.GetBySlugAsync(slug, ct);
            if (existing is not null)
                return Results.Json(new { error = "slug_taken", slug }, statusCode: StatusCodes.Status409Conflict);

            TenantRecord created;
            try
            {
                created = await registry.CreateTenantAsync(slug, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // UNIQUE(slug) race loser.
                return Results.Json(new { error = "slug_taken", slug }, statusCode: StatusCodes.Status409Conflict);
            }

            // Eagerly provision the shard so the tenant is immediately usable.
            await shards.GetOrCreateShardAsync(created.TenantId, ct);
            return Results.Created($"/api/tenants/{slug}", new { tenantId = created.TenantId, slug });
        });

        tenants.MapGet("/{slug}", async (HttpContext http, string slug, TenantRegistry registry, CancellationToken ct) =>
        {
            if (!IsAdmin(http)) return Results.Json(new { error = "admin_token_required" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!SlugValidator.IsValid(slug, out _)) return Results.NotFound();
            var tenant = await registry.GetBySlugAsync(slug, ct);
            return tenant is null ? Results.NotFound() : Results.Ok(new { tenantId = tenant.TenantId, slug = tenant.Slug, createdAt = tenant.CreatedAt, pinned = tenant.Pinned });
        });

        tenants.MapDelete("/{slug}", async (HttpContext http, string slug, TenantRegistry registry, TenantShardRegistry shards, CancellationToken ct) =>
        {
            if (!IsAdmin(http)) return Results.Json(new { error = "admin_token_required" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!SlugValidator.IsValid(slug, out _)) return Results.NotFound();

            var tenant = await registry.GetBySlugAsync(slug, ct);
            if (tenant is null) return Results.NotFound();

            await shards.EvictShardAsync(tenant.TenantId);
            await registry.DeleteTenantAsync(tenant.TenantId, ct);

            var dir = Path.Combine(shards.ShardRoot, tenant.TenantId);
            long bytesFreed = 0;
            if (Directory.Exists(dir))
            {
                bytesFreed = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                Directory.Delete(dir, recursive: true);
            }

            return Results.Ok(new DisposalReceipt(tenant.TenantId, bytesFreed, TablesDropped: 2));
        });

        return app;
    }

    private static bool IsAdmin(HttpContext http)
    {
        var expected = Environment.GetEnvironmentVariable("AGENTFOLIO_ADMIN_TOKEN");
        if (string.IsNullOrEmpty(expected)) return false;
        return string.Equals(
            http.Request.Headers["X-Admin-Token"].ToString(),
            expected,
            StringComparison.Ordinal);
    }
}

public sealed record CreateTenantRequest(string Slug);

public sealed record DisposalReceipt(string TenantId, long BytesFreed, int TablesDropped);

public static class SlugValidator
{
    /// <summary>3-40 chars, [a-z0-9-]; rejects traversal, absolute paths, null bytes.</summary>
    public static bool IsValid(string? slug, out string reason)
    {
        reason = "";
        if (slug is null) { reason = "missing"; return false; }
        if (slug.Contains('\0')) { reason = "null_byte"; return false; }
        if (slug.Contains('/') || slug.Contains('\\')) { reason = "path_separator"; return false; }
        if (slug.Contains("..")) { reason = "traversal"; return false; }
        if (Path.IsPathRooted(slug)) { reason = "absolute_path"; return false; }
        if (slug.Length < 3 || slug.Length > 40) { reason = "length"; return false; }
        foreach (var c in slug)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
            {
                reason = "charset";
                return false;
            }
        }
        return true;
    }
}
