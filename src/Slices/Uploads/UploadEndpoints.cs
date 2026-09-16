using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Slices.Uploads;

/// <summary>
/// Upload API (spec 002 s3-03). All endpoints RequireAuthorization.
/// POST /api/uploads  — multipart form file, tenant from tid claim, session from
///                      af_sid cookie (GUID cookie minted when absent).
/// DELETE /api/session/data — wipes the session prefix, returns { deleted }.
/// </summary>
public static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadsApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/uploads", async (HttpContext http, UploadService uploads, CancellationToken ct) =>
        {
            var tid = http.User.FindFirst("tid")?.Value;
            if (string.IsNullOrEmpty(tid))
                return Results.Json(new { error = "missing_tid_claim" }, statusCode: StatusCodes.Status403Forbidden);

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
                return Results.Ok(receipt);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
        }).DisableAntiforgery().RequireAuthorization();

        app.MapDelete("/api/session/data", async (HttpContext http, UploadService uploads, CancellationToken ct) =>
        {
            var tid = http.User.FindFirst("tid")?.Value;
            if (string.IsNullOrEmpty(tid))
                return Results.Json(new { error = "missing_tid_claim" }, statusCode: StatusCodes.Status403Forbidden);

            var sid = GetOrCreateSession(http);
            if (sid is null) return Results.Json(new { error = "session_cookie_failed" }, statusCode: StatusCodes.Status500InternalServerError);

            var deleted = await uploads.DeleteSessionDataAsync(tid, sid, ct);
            return Results.Ok(new { deleted });
        }).RequireAuthorization();

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
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
        });
        return sid;
    }
}
