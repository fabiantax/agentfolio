using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Slices.Auth2;

/// <summary>
/// GitHub OAuth endpoints (spec 002 s2-02): /auth/github/start -> GitHub authorize
/// with PKCE; /auth/github/callback validates state, exchanges the code, provisions
/// the user tenant and signs in via the existing cookie scheme. Config comes from
/// env only (GITHUB_CLIENT_ID / GITHUB_CLIENT_SECRET); unset => 503.
/// </summary>
public static class Auth2Endpoints
{
    public const string OAuthCookieName = "af_oauth";
    private static readonly TimeSpan OAuthCookieLifetime = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapAuth2Api(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/github/start", async (
            HttpContext http, IHttpClientFactory httpClientFactory) =>
        {
            var clientId = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET");
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                return Results.Json(new { error = "github_oauth_not_configured" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var state = GitHubOauth.NewState();
            var verifier = GitHubOauth.NewVerifier();
            var challenge = GitHubOauth.CodeChallengeS256(verifier);

            http.Response.Cookies.Append(OAuthCookieName,
                $"{state}.{verifier}",
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = OAuthCookieLifetime,
                });

            var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}/auth/github/callback";
            var url = GitHubOauth.BuildStartUrl(clientId, redirectUri, state, challenge);
            return Results.Redirect(url, permanent: false);
        });

        app.MapGet("/auth/github/callback", async (
            HttpContext http, IHttpClientFactory httpClientFactory,
            TenantRegistry registry, string? code, string? state, CancellationToken ct) =>
        {
            var cookie = http.Request.Cookies[OAuthCookieName];
            if (cookie is null || state is null || code is null)
                return Results.Json(new { error = "oauth_state_missing" },
                    statusCode: StatusCodes.Status400BadRequest);

            var sep = cookie.IndexOf('.');
            if (sep <= 0) return Results.Json(new { error = "oauth_state_invalid" },
                statusCode: StatusCodes.Status400BadRequest);
            var expectedState = cookie[..sep];
            var verifier = cookie[(sep + 1)..];

            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(state),
                    System.Text.Encoding.ASCII.GetBytes(expectedState)))
            {
                return Results.Json(new { error = "oauth_state_mismatch" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var clientId = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID")!;
            var clientSecret = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET")!;
            var github = httpClientFactory.CreateClient("github");

            var accessToken = await GitHubOauth.ExchangeCodeAsync(
                github, clientId, clientSecret, code,
                $"{http.Request.Scheme}://{http.Request.Host}/auth/github/callback",
                verifier);
            var user = await GitHubOauth.GetUserAsync(github, accessToken);
            var tenant = await registry.GetOrCreateUserTenantAsync(user.Id, ct);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new("sub", user.Id.ToString()),
                new("tid", tenant.TenantId),
            };
            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims,
                    CookieAuthenticationDefaults.AuthenticationScheme)));

            http.Response.Cookies.Delete(OAuthCookieName);
            return Results.Redirect("/", permanent: false);
        });

        app.MapMethods("/login", ["GET", "HEAD"], () => Results.Content(
            """
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Sign in - AgentFolio</title></head>
            <body>
              <h1>Sign in to AgentFolio</h1>
              <a href="/auth/github/start">Sign in with GitHub</a>
            </body>
            </html>
            """, "text/html"));

        return app;
    }
}
