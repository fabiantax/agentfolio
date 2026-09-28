using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
    /// <summary>Public repo link (evidence bar, issue #42).</summary>
    public const string RepoUrl = "http://100.112.37.119:3200/fabiantax/agentfolio";

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
.evidence { margin-top: 24px; background: var(--af-surface); border: 1px solid var(--af-border);
  border-radius: var(--af-radius-lg); padding: 20px 24px; }
.ev-title { font: 500 0.6875rem/1.4 var(--af-mono); letter-spacing: 0.08em;
  text-transform: uppercase; color: var(--af-text-2); margin-bottom: 12px; }
.evidence ul { margin: 0; padding-left: 18px; color: var(--af-text-2); font-size: 0.8125rem; line-height: 1.7; }
.evidence a { color: var(--af-accent); text-decoration: none; font-weight: 500; }
.evidence a:hover { text-decoration: underline; }
@media (max-width: 640px) { .wrap { padding: 24px 16px; } }
</style>
""";


    public static IEndpointRouteBuilder MapShareApi(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/share/{slug}", ["GET", "HEAD"], async (HttpContext http, string slug, TenantRegistry registry, TenantShardRegistry shards, CancellationToken ct) =>
        {
            TenantRecord? tenant;
            try
            {
                tenant = await ResolveTenantAsync(registry, slug, ct);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // Registry store failure (locked, corrupt, unreadable): fail loud with a
                // NAMED dependency error — never an unhandled 500 stack trace (#45).
                return Results.Json(new { error = "tenancy_store_unavailable", detail = ex.GetType().Name }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (tenant is null)
                return slug == "showcase"
                    ? Results.Json(new { error = "showcase_not_seeded" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.NotFound();

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
            return Results.Content(PortfolioPage(slug, tenant.TenantId, HasAnyStars(registry, shards, tenant.TenantId)), "text/html");
        });

        app.MapPost("/share/{slug}/unlock", async (HttpContext http, string slug, TenantRegistry registry, TenantShardRegistry shards, CancellationToken ct) =>
        {
            TenantRecord? tenant;
            try
            {
                tenant = await ResolveTenantAsync(registry, slug, ct);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                return Results.Json(new { error = "tenancy_store_unavailable", detail = ex.GetType().Name }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (tenant is null)
                return slug == "showcase"
                    ? Results.Json(new { error = "showcase_not_seeded" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.NotFound();

            var pwHash = await registry.GetSharePasswordHashAsync(tenant.TenantId, ct);
            var form = await http.Request.ReadFormAsync(ct);
            var password = form["password"].ToString();

            // Unknown/absent hash or wrong password → 401 (constant-ish behavior);
            // body is the styled gate page with an inline error (never a raw trace).
            if (pwHash is null || !PasswordGate.VerifyPassword(password, pwHash))
            {
                http.Response.Headers["X-Robots-Tag"] = "noindex";
                return Results.Content(GatePage(slug, error: true), "text/html", contentEncoding: null, statusCode: StatusCodes.Status401Unauthorized);
            }

            http.Response.Cookies.Append(ShareCookieName(tenant.TenantId), "1", new CookieOptions
            {
                HttpOnly = true,
                Expires = DateTimeOffset.UtcNow.AddHours(24),
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps, // tailnet-http demo drops Secure cookies
            });

            // Sign the share visitor in for /api/uploads + /chat (both
            // RequireAuthorization): the share visitor IS authenticated for
            // this tenant — tenant id comes from the REGISTRY row, never from
            // client input, so the tid claim cannot be forged here. Widget
            // uploads/chat then resolve to this tenant (Uploads
            // ResolveTenantIdAsync reads the tid claim).
            var sharePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, $"share:{tenant.TenantId}"), new Claim("tid", tenant.TenantId)],
                CookieAuthenticationDefaults.AuthenticationScheme));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, sharePrincipal);

            http.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Content(PortfolioPage(slug, tenant.TenantId, HasAnyStars(registry, shards, tenant.TenantId)), "text/html");
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

    private static string GatePage(string slug, bool error = false)
    {
        var enc = HttpUtility.HtmlEncode(slug);
        var errorHtml = error
            ? "\n      <p class=\"error\" role=\"alert\">Incorrect password — try again.</p>"
            : string.Empty;
        return $$"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>{{enc}} — protected portfolio</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@500&display=swap" rel="stylesheet">
{{TokenCss}}
<style>
.gate { max-width: 380px; margin: 96px auto 0; text-align: center; }
.gate .lock { font: 500 0.6875rem/1.4 var(--af-mono); letter-spacing: 0.08em;
  text-transform: uppercase; color: var(--af-accent); margin-bottom: 12px; }
.gate h1 { font-size: 1.75rem; margin-bottom: 6px; }
.gate p { color: var(--af-text-2); font-size: 0.875rem; margin: 0 0 24px; }
.gate form { background: var(--af-surface); border: 1px solid var(--af-border);
  border-radius: var(--af-radius-lg); padding: 24px; display: flex; flex-direction: column; gap: 12px; }
.gate input[type="password"] { padding: 10px 14px; border: 1px solid var(--af-border);
  border-radius: var(--af-radius-md); font: inherit; color: var(--af-text);
  background: #0B0F14; outline: none; text-align: center; }
.gate input[type="password"]:focus { border-color: var(--af-accent); }
.gate button { background: var(--af-accent-btn); color: #fff; border: 0;
  border-radius: var(--af-radius-md); padding: 10px 16px; font: 600 0.875rem/1 var(--af-font);
  cursor: pointer; transition: background 0.15s ease; }
.gate button:hover { background: var(--af-accent-btn-hover); }
.gate .error { color: var(--af-danger); font-size: 0.8125rem; margin: 0 0 12px; }
@media (max-width: 640px) { .gate { margin-top: 64px; } }
</style>
</head>
<body>
  <main class="wrap">
    <div class="gate">
      <div class="lock">AgentFolio · Private link</div>
      <h1>{{enc}}</h1>
      <p>This portfolio is password protected.</p>{{errorHtml}}
      <form method="post" action="/share/{{HttpUtility.UrlEncode(slug)}}/unlock">
        <input type="password" name="password" placeholder="Password" autocomplete="current-password" required autofocus />
        <button type="submit">Unlock</button>
      </form>
    </div>
  </main>
  <script>
    // The unlock POST signed the visitor in (auth cookie) — uploads and chat
    // just work. No client-side credential handling needed here.
  </script>
</body>
</html>
""";
    }

    /// <summary>
    /// Capability chips that reflect REALITY (issue #41 AC4): data chips only when the
    /// tenant actually has synced content; always-safe capability chips otherwise.
    /// </summary>
    /// <summary>True when the tenant shard has at least one starred repo (never throws).</summary>
    private static bool HasAnyStars(TenantRegistry registry, TenantShardRegistry shards, string tenantId)
    {
        try
        {
            var shard = shards.GetOrCreateShardAsync(tenantId).GetAwaiter().GetResult();
            var any = false;
            foreach (var _ in shard.QueryStarsAsync().ToBlockingEnumerable()) { any = true; break; }
            return any;
        }
        catch { return false; }
    }

    private static string DataChips(string? hasData)
    {
        var chip = (string label) => $"<span class=\"chip\">{label}</span>";
        return string.IsNullOrEmpty(hasData)
            ? string.Join("\n        ", chip("DOC UPLOAD"), chip("SSE STREAMING"), chip("BROWSER-AI TIER"))
            : string.Join("\n        ", chip("GITHUB STARS"), chip("LINKEDIN SAVES"), chip("DOC UPLOAD"), chip("SSE STREAMING"));
    }

    private static string PortfolioPage(string slug, string tenantId, bool hasData)
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
        {{DataChips(hasData ? "yes" : null)}}
      </div>
    </header>
    <section class="card">
      <agentfolio-chat server-url=""></agentfolio-chat>
    </section>
    <aside class="evidence">
      <div class="ev-title">VERIFY THIS DEMO</div>
      <ul>
        <li><a href="{{RepoUrl}}" rel="noopener noreferrer">Source &amp; history</a> — every commit, PR and review that built this page</li>
        <li><a href="{{RepoUrl}}/actions">CI pipeline</a> — build 0-warnings, 112 unit + 2 E2E + 20 web tests, coverage line 72.8% / branch 65.2%</li>
        <li>Eval — <a href="{{RepoUrl}}/raw/branch/main/eval/results/v2-citations.md">measured, 20 real questions</a>: citation-exactness 85% (was 0% pre-wiring; q03/q16/q19 misses documented), entity-F1 0.71, p50 4.3s on a local 4B model</li>
        <li>Architecture — .NET 10 vertical slices, CQRS-lite + event-log sourcing, Atlas graph-enforced boundaries, sandboxed doc extraction</li>
        <li>Deploy — Aspire orchestration, Helm/k8s target, self-hosted Git+CI on Tailscale</li>
      </ul>
    </aside>
    <footer>Built with .NET 10 · Microsoft Agent Framework · SQLite vec/FTS · <a href="/share/showcase">AgentFolio</a></footer>
  </main>
    <script type="module" src="{{ChatScriptPlaceholderSrc}}"></script>
    <script>
      // The unlock POST signs the share visitor in (auth cookie with the
      // tenant tid claim) — uploads and chat just work with the cookie. No
      // client-side credential handling needed.
    </script>
  </body>
</html>
""";
    }
}
