using System.Linq;
using AgentFolio.Platform.Storage;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Slices.Uploads;

/// <summary>
/// Upload API (spec 002 s3-03). All endpoints RequireAuthorization.
/// POST /api/uploads  — multipart form file, tenant from tid claim, session from
///                      af_sid cookie (GUID cookie minted when absent).
/// DELETE /api/session/data — wipes the session prefix, returns { deleted }.
/// </summary>
public static class UploadEndpoints
{
    /// <summary>Dev/test observability: last extraction failure reason (fail-fast diagnosis, not a log dive).</summary>
    internal static volatile string? LastExtractionError;
    /// <summary>
    /// OAuth users carry a tid claim. Password-gated share visitors do not — they
    /// upload into the SHOWCASE tenant (spec 002 FR4a: share context is the tenant
    /// whose page they're on; v1.1 demo scope = showcase).
    /// </summary>
    private static async Task<string?> ResolveTenantIdAsync(HttpContext http, CancellationToken ct)
    {
        var claim = http.User.FindFirst("tid")?.Value;
        if (!string.IsNullOrEmpty(claim)) return claim;
        var registry = http.RequestServices.GetService<TenantRegistry>();
        if (registry is null) return null;
        var showcase = await registry.GetBySlugAsync("showcase", ct);
        return showcase?.TenantId;
    }

    private static async Task<IResult> DocsAsync(HttpContext http, IObjectStore store, CancellationToken ct)
    {
        var tid = await ResolveTenantIdAsync(http, ct);
        if (tid is null) return Results.Json(new { error = "tenant_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        var sid = GetOrCreateSession(http);
        var prefix = $"tenants/{tid}/sessions/{sid}/extracted/";
        var keys = await store.ListByPrefixAsync(prefix, ct);
        var docs = new List<object>();
        foreach (var k in keys)
        {
            await using var stream = await store.GetAsync(k, ct);
            if (stream is null) continue;
            using var r = new StreamReader(stream);
            var text = await r.ReadToEndAsync(ct);
            docs.Add(new { key = k, text });
        }
        var uploads = await store.ListByPrefixAsync($"tenants/{tid}/sessions/{sid}/", ct);
        return Results.Ok(new
        {
            sessionId = sid,
            documents = docs,
            diagnostics = new
            {
                uploadCount = uploads.Count(k => !k.Contains("/extracted/")),
                extractedCount = docs.Count,
                lastExtractionError = LastExtractionError,
            },
        });
    }

    public static IEndpointRouteBuilder MapUploadsApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/uploads", async (HttpContext http, UploadService uploads, CancellationToken ct) =>
        {
            var tid = await ResolveTenantIdAsync(http, ct);
            if (tid is null)
                return Results.Json(new { error = "tenant_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var sid = GetOrCreateSession(http);
            if (sid is null) return Results.Json(new { error = "session_cookie_failed" }, statusCode: StatusCodes.Status500InternalServerError);

            var file = http.Request.Form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.Json(new { error = "no_file" }, statusCode: StatusCodes.Status400BadRequest);
            if (file.Length > UploadService.MaxBytes)
                return Results.Json(new { error = "upload_too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            try
            {
                var receipt = await uploads.StoreAsync(tid, sid, ms.ToArray(), DateTimeOffset.UtcNow, ct);

                // s4-04: fire-and-forget extraction in the process sandbox.
                // Capture SINGLETONS now: the request scope (http.RequestServices) is
                // disposed when the response completes — using it in the background task
                // throws ObjectDisposedException (IFeatureCollection has been disposed).
                var extraction = http.RequestServices.GetRequiredService<ExtractionService>();
                var uploadKey = receipt.Key;
                string? extractionError = null;
                var extractNow = http.Request.Query.ContainsKey("sync");
                var run = async () =>
                {
                    try { await extraction.ExtractAsync(tid, sid, uploadKey, onError: r => LastExtractionError = r); }
                    catch (Exception ex) { LastExtractionError = $"extraction_dispatch_error: {ex.Message}"; }
                };

                if (extractNow)
                {
                    await run();
                    extractionError = LastExtractionError;
                    return Results.Ok(new
                    {
                        receipt.Key,
                        receipt.Bytes,
                        receipt.ContentType,
                        receipt.ExpiresAt,
                        extracted = extractionError is null,
                        extractionError,
                    });
                }

                _ = Task.Run(run, CancellationToken.None);
                return Results.Ok(receipt);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
        }).DisableAntiforgery().RequireAuthorization();

        app.MapDelete("/api/session/data", async (HttpContext http, UploadService uploads, CancellationToken ct) =>
        {
            var tid = await ResolveTenantIdAsync(http, ct);
            if (tid is null)
                return Results.Json(new { error = "tenant_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var sid = GetOrCreateSession(http);
            if (sid is null) return Results.Json(new { error = "session_cookie_failed" }, statusCode: StatusCodes.Status500InternalServerError);

            var deleted = await uploads.DeleteSessionDataAsync(tid, sid, ct);
            return Results.Ok(new { deleted });
        }).RequireAuthorization();

        app.MapGet("/api/session/docs", async (HttpContext http, IObjectStore store, CancellationToken ct)
            => await DocsAsync(http, store, ct));

        return app;
    }

    private static string? GetOrCreateSession(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue("af_sid", out var existing) && !string.IsNullOrEmpty(existing))
            return existing;

        var sid = Guid.NewGuid().ToString("N");
        http.Response.Cookies.Append("af_sid", sid, new CookieOptions
        {
            HttpOnly = true,
            // Secure ONLY over https: hardcoding true silently drops the cookie on the
            // plain-HTTP tailnet demo (and the test host) — every request would mint a
            // NEW session, orphaning uploads. SameAsRequest semantics, explicit.
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
        });
        return sid;
    }
}
