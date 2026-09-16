using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using System.Text;
using System.Web;

namespace Slices.Share;

/// <summary>
/// s5-05 public share URLs. GET /share/{slug} resolves the tenant ONLY via the
/// registry's slug → tenant_id mapping — client input (cookies, claims, query,
/// form fields) never influences which shard is touched. Gated tenants show a
/// password page (Argon2id verify, af_share_{tid} cookie 24h HttpOnly).
/// All responses carry X-Robots-Tag noindex (also set globally in Program.cs).
/// </summary>
public static class ShareEndpoints
{
    public const string ChatScriptPlaceholderSrc = "/web/chat-core/index.js"; // served from wwwroot via MapStaticAssets

    public static IEndpointRouteBuilder MapShareApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/share/{slug}", async (HttpContext http, string slug, TenantRegistry registry, TenantShardRegistry shards, CancellationToken ct) =>
        {
            var tenant = await ResolveTenantAsync(registry, slug, ct);
            if (tenant is null) return Results.NotFound();

            var pwHash = await registry.GetSharePasswordHashAsync(tenant.TenantId, ct);
            if (pwHash is not null)
            {
                var cookieName = ShareCookieName(tenant.TenantId);
                if (!http.Request.Cookies.ContainsKey(cookieName))
                {
                    http.Response.Headers["X-Robots-Tag"] = "noindex";
                    return Results.Content(GatePage(slug), "text/html");
                }
            }

            http.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Content(PortfolioPage(slug, tenant.TenantId), "text/html");
        });

        app.MapPost("/share/{slug}/unlock", async (HttpContext http, string slug, TenantRegistry registry, CancellationToken ct) =>
        {
            var tenant = await ResolveTenantAsync(registry, slug, ct);
            if (tenant is null) return Results.NotFound();

            var pwHash = await registry.GetSharePasswordHashAsync(tenant.TenantId, ct);
            var form = await http.Request.ReadFormAsync(ct);
            var password = form["password"].ToString();

            // Unknown/absent hash or wrong password → 401 (constant-ish behavior).
            if (pwHash is null || !PasswordGate.VerifyPassword(password, pwHash))
                return Results.Json(new { error = "invalid_password" }, statusCode: StatusCodes.Status401Unauthorized);

            http.Response.Cookies.Append(ShareCookieName(tenant.TenantId), "1", new CookieOptions
            {
                HttpOnly = true,
                Expires = DateTimeOffset.UtcNow.AddHours(24),
                SameSite = SameSiteMode.Lax,
                Secure = true,
            });
            http.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Content(PortfolioPage(slug, tenant.TenantId), "text/html");
        });

        return app;
    }

    /// <summary>
    /// THE isolation boundary: shard tenant id comes exclusively from the global
    /// registry row for the slug. Any forged tid (cookie, claim, query) is ignored.
    /// </summary>
    public static async Task<TenantRecord?> ResolveTenantAsync(
        TenantRegistry registry, string slug, CancellationToken ct = default)
    {
        if (slug is null || slug.Length < 3 || slug.Length > 40
            || slug.Contains('/') || slug.Contains('\\') || slug.Contains("..") || slug.Contains('\0')
            || slug.Any(c => !((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')))
            return null;
        return await registry.GetBySlugAsync(slug, ct);
    }

    internal static string ShareCookieName(string tenantId) => $"af_share_{tenantId}";

    private static string GatePage(string slug) => $"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><meta name="robots" content="noindex"><title>{HttpUtility.HtmlEncode(slug)} - AgentFolio</title></head>
<body>
  <h1>{HttpUtility.HtmlEncode(slug)}</h1>
  <p>This portfolio is password protected.</p>
  <form method="post" action="/share/{HttpUtility.UrlEncode(slug)}/unlock">
    <input type="password" name="password" placeholder="Password" required />
    <button type="submit">Unlock</button>
  </form>
</body>
</html>
""";

    private static string PortfolioPage(string slug, string tenantId)
    {
        var enc = HttpUtility.HtmlEncode(slug);
        var tid = HttpUtility.HtmlEncode(tenantId);
        return $"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><meta name="robots" content="noindex"><title>{enc} - AgentFolio</title></head>
<body>
  <h1>{enc}</h1>
  <agentfolio-chat data-api-base="/api" data-tenant-id="{tid}"></agentfolio-chat>
  <script src="{ChatScriptPlaceholderSrc}"></script>
</body>
</html>
""";
    }
}
