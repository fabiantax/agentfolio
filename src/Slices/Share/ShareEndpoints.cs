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
    /// <summary>Design tokens (DESIGN.md, AA-verified) shared by all share pages.</summary>
    private const string TokenCss = """
<style>
:root {
  --af-bg: #0D1117; --af-surface: #151B23; --af-raised: #1C2430; --af-border: #2A3441;
  --af-text: #E6EDF3; --af-text-2: #9BA8B7; --af-accent: #4C8DFF; --af-accent-btn: #2B67D4;
  --af-accent-btn-hover: #2E6ADB; --af-success: #3FB950; --af-danger: #F85149;
  --af-radius-md: 10px; --af-radius-lg: 14px;
  --af-font: 'Inter', system-ui, -apple-system, sans-serif;
  --af-mono: 'JetBrains Mono', ui-monospace, 'Cascadia Code', monospace;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--af-bg); color: var(--af-text);
  font: 0.9375rem/1.55 var(--af-font); -webkit-font-smoothing: antialiased; }
.wrap { max-width: 880px; margin: 0 auto; padding: 48px 24px; }
.hero { margin-bottom: 24px; }
.eyebrow { font: 500 0.6875rem/1.4 var(--af-mono); letter-spacing: 0.08em;
  text-transform: uppercase; color: var(--af-accent); margin-bottom: 8px; }
h1 { font-size: 1.75rem; font-weight: 700; letter-spacing: -0.02em; line-height: 1.15; margin: 0 0 6px; }
.sub { color: var(--af-text-2); font-size: 0.875rem; margin: 0; }
.meta { display: flex; gap: 8px; margin-top: 12px; flex-wrap: wrap; }
.chip { font: 500 0.6875rem/1.4 var(--af-mono); letter-spacing: 0.06em; color: var(--af-text-2);
  background: var(--af-raised); border: 1px solid var(--af-border); border-radius: 999px; padding: 4px 10px; }
.card { background: var(--af-surface); border: 1px solid var(--af-border);
  border-radius: var(--af-radius-lg); overflow: hidden; }
footer { margin-top: 24px; color: var(--af-text-2); font-size: 0.75rem; }
footer a { color: var(--af-accent); text-decoration: none; }
@media (max-width: 640px) { .wrap { padding: 24px 16px; } }
</style>
""";


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
                Secure = http.Request.IsHttps, // tailnet-http demo drops Secure cookies
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
        return $$"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>{{enc}} — portfolio chat</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@500&display=swap" rel="stylesheet">
{{TokenCss}}
</head>
<body>
  <main class="wrap">
    <header class="hero">
      <div class="eyebrow">Portfolio · AI-grounded</div>
      <h1>{{enc}}</h1>
      <p class="sub">Ask about GitHub stars, saved posts, and uploaded documents — every answer cites its source.</p>
      <div class="meta">
        <span class="chip">GITHUB STARS</span>
        <span class="chip">LINKEDIN SAVES</span>
        <span class="chip">DOC UPLOAD</span>
        <span class="chip">SSE STREAMING</span>
      </div>
    </header>
    <section class="card">
      <agentfolio-chat server-url=""></agentfolio-chat>
    </section>
    <footer>Built with .NET 10 · Microsoft Agent Framework · SQLite vec/FTS · <a href="/share/showcase">AgentFolio</a></footer>
  </main>
  <script type="module" src="{{ChatScriptPlaceholderSrc}}"></script>
</body>
</html>
""";
    }
}
