using System.Security.Claims;
using AgentFolio.Agents;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Slices.Auth2;
using Slices.Chat;
using Slices.Tenancy;

var builder = WebApplication.CreateBuilder(args);

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

app.MapChatApi();
app.MapTenancyApi();
app.MapAuth2Api();

app.Run();

public partial class Program
{
}

record LoginRequest(string Password);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
