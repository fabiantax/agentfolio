using System.Security.Claims;
using AgentFolio.Agents;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Slices.Auth2;
using Slices.Chat;
using Slices.Uploads;
using AgentFolio.Platform.Storage;
using Slices.Tenancy;
using Slices.Share;
using AgentFolio.Platform.Migrations;

var builder = WebApplication.CreateBuilder(args);

// s5-05 one-shot migration: --migrate-single-to-tenant (checked BEFORE builder.Build).
if (args.Contains("--migrate-single-to-tenant"))
{
    var sourcePath = Environment.GetEnvironmentVariable("AGENTFOLIO_DB_PATH");
    var globalDb = Environment.GetEnvironmentVariable("AGENTFOLIO_GLOBAL_DB");
    if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(globalDb))
    {
        Console.Error.WriteLine("error: --migrate-single-to-tenant requires AGENTFOLIO_DB_PATH (source db) and AGENTFOLIO_GLOBAL_DB env vars.");
        Environment.Exit(2);
    }
    var migRegistry = new TenantRegistry(globalDb);
    using var migShards = new TenantShardRegistry(globalDb);
    var result = await MigrateSingleToTenant.Run(sourcePath, migRegistry, migShards);
    Console.WriteLine($"migration: stars={result.Stars} saves={result.Saves} events={result.Events} tenantId={result.TenantId}");
    Environment.Exit(0);
}

// Chat: keyed 'nim' IChatClient (falls back inline when NIM env unset) + conversation store.
builder.Services.AddNimChatClient();
builder.Services.AddSingleton<IConversationStore>(_ =>
    new SqliteConversationStore(
        Environment.GetEnvironmentVariable("AGENTFOLIO_DB_PATH") ?? "agentfolio.db"));

// Add services to the container.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "agentfolio_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.LoginPath = null;
    });
builder.Services.AddSingleton<ILoginRateLimiter, LoginRateLimiter>();
builder.Services.AddAuthorization();
builder.Services.AddHttpClient();

// Tenancy (spec 002 s1-01): global registry + per-tenant shard LRU.
builder.Services.AddSingleton(_ =>
    new TenantRegistry(Environment.GetEnvironmentVariable("AGENTFOLIO_GLOBAL_DB")
        ?? Path.Combine(AppContext.BaseDirectory,
            Path.GetFileName(Environment.GetEnvironmentVariable("AGENTFOLIO_DB_PATH") ?? "agentfolio.db")
                .Replace("agentfolio.db", "agentfolio-global.db"))));
builder.Services.AddSingleton(sp => new TenantShardRegistry(
    sp.GetRequiredService<TenantRegistry>().GlobalDbPath));

// Uploads (spec 002 s3-03): bucket store (S3 when configured; InMemory for local dev).
if (Environment.GetEnvironmentVariable(S3ObjectStore.EndpointEnvVar) is string)
{
    builder.Services.AddSingleton<IObjectStore, S3ObjectStore>();
}
else if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test"))
{
    // Dev/Test ONLY (AGENTS.md rule 1): ephemeral store, loud warning. In Production we
    // never silently accept uploads that evaporate on restart — S3 env is required.
    Console.Error.WriteLine("WARNING: AGENTFOLIO_S3_ENDPOINT unset — using in-memory object store (DEV/TEST ONLY; uploads do not survive restart).");
    builder.Services.AddSingleton<IObjectStore, InMemoryObjectStore>();
}
else
{
    throw new InvalidOperationException(
        "AGENTFOLIO_S3_ENDPOINT (and bucket/keys) must be set in Production — no silent in-memory fallback (AGENTS.md rule 1).");
}
builder.Services.AddSingleton<UploadService>();
builder.Services.AddSingleton<ExtractionService>();
builder.Services.AddHostedService<UploadExpiryEnforcer>();

var app = builder.Build();

// Configure the HTTP request pipeline.

// No indexing of any AgentFolio response.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();

app.UseHttpsRedirection();

app.MapPost("/auth/login", async (HttpContext ctx, LoginRequest? request, ILoginRateLimiter rateLimiter) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var now = DateTimeOffset.UtcNow;

    var gate = rateLimiter.CheckAndRecord(ip, now);
    if (!gate.Allowed)
    {
        return Results.Json(new { error = "locked", retryAfterSeconds = gate.RetryAfterSeconds }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var encodedHash = Environment.GetEnvironmentVariable(PasswordGate.HashEnvVar);
    if (request is null || encodedHash is null || !PasswordGate.VerifyPassword(request.Password, encodedHash))
    {
        rateLimiter.RecordFailure(ip, now);
        var state = rateLimiter.CheckAndRecord(ip, now);
        return Results.Json(new { error = "invalid_password", attemptCount = state.AttemptCount }, statusCode: StatusCodes.Status401Unauthorized);
    }

    rateLimiter.Reset(ip);
    await ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.NoContent();
});

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
});

// Landing: the app IS the showcase portfolio chat.
app.MapGet("/", () => Results.Redirect("/share/showcase", permanent: false));

app.MapChatApi();

// Static UI: /web/* serves the built chat component + host demos (same origin as API).
app.MapStaticAssets();
app.MapUploadsApi();
app.MapTenancyApi();
app.MapAuth2Api();
app.MapShareApi();

app.Run();

public partial class Program
{
}

record LoginRequest(string Password);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
