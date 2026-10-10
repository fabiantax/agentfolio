using System.Security.Claims;
using AgentFolio.Agents;
using AgentFolio.Platform.Auth;
using AgentFolio.Platform.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenTelemetry.Exporter;
using OpenTelemetry.Exporter.OpenTelemetryProtocol;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Slices.Auth2;
using Slices.Chat;
using Slices.Uploads;
using AgentFolio.Platform.Storage;
using Slices.Tenancy;
using Slices.Share;
using Slices.Stars;
using AgentFolio.Platform.Migrations;
using Microsoft.AspNetCore.HttpLogging;
using AgentFolio.Api;

var builder = WebApplication.CreateBuilder(args);
// Fail-fast DI: validate the whole graph at build (dev/test). Bad registrations
// (missing deps, captive deps, scoped-in-singleton) throw at startup, not at first request.
if (!builder.Environment.IsProduction())
{
    builder.Host.UseDefaultServiceProvider(o =>
    {
        o.ValidateOnBuild = true;
        o.ValidateScopes = true;
    });
}

// Production container contract (#100): all required env present or the process
// dies HERE with a named list — never a lazy first-request failure (#100 AC).
ProductionStartupValidator.Validate(builder.Environment.IsProduction());

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

// Stars: GitHub stars client from AGENTFOLIO_GITHUB_TOKEN (scoped, overridable in tests).
// NER/RE extraction + GraphFusion ingest (#66 stage B wiring). All optional:
// absent env = extraction stage skips (logged), sync/backfill unaffected.
builder.Services.AddSingleton(sp =>
{
    var gliner = Environment.GetEnvironmentVariable("GLINER_URL") ?? "http://127.0.0.1:8095";
    var relex = Environment.GetEnvironmentVariable("RELEX_URL") ?? "http://127.0.0.1:8090";
    var gf = Environment.GetEnvironmentVariable("GF_A2A_URL") ?? "http://100.112.37.119:50052";
    return new Slices.Stars.Extraction.GlinerBatchClient(gliner, timeoutSeconds: 300);
});
builder.Services.AddSingleton(sp =>
{
    var relex = Environment.GetEnvironmentVariable("RELEX_URL") ?? "http://127.0.0.1:8090";
    return new Slices.Stars.Extraction.RelexClient(relex);
});
builder.Services.AddSingleton(sp =>
{
    var gf = Environment.GetEnvironmentVariable("GF_A2A_URL") ?? "http://100.112.37.119:50052";
    return new Slices.Stars.Graph.GraphFusionClient(gf);
});

// Stars retrieval for chat (#66 stage D): GF recall + ColBERT rerank over the
// stars corpus. Degrade-open — if the services are absent, SearchAsync returns
// empty and chat proceeds uncited (IStarsSearch contract).
builder.Services.AddSingleton(sp =>
{
    var gf = Environment.GetEnvironmentVariable("GF_A2A_URL") ?? "http://100.112.37.119:50052";
    var colbert = Environment.GetEnvironmentVariable("COLBERT_URL") ?? "http://127.0.0.1:8092";

    // Winner-arm recall hay (#123): the tenant's own stars_raw enrichment. Corpus
    // db = AGENTFOLIO_CORPUS_DB, else the SINGLE tenant shard under the global
    // db's tenants/ dir; ambiguous (multi-shard) or absent = DISABLED with a loud
    // warning, never a guess (rule 1: no silent substitution).
    Slices.Stars.Retrieval.EnrichedRecallProvider? enriched = null;
    var corpusPath = Environment.GetEnvironmentVariable("AGENTFOLIO_CORPUS_DB");
    var globalDb = Environment.GetEnvironmentVariable("AGENTFOLIO_GLOBAL_DB");
    if (string.IsNullOrEmpty(corpusPath) && !string.IsNullOrEmpty(globalDb))
    {
        var tenantsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(globalDb))!, "tenants");
        if (Directory.Exists(tenantsDir))
        {
            var shards = Directory.GetDirectories(tenantsDir)
                .Select(d => Path.Combine(d, "data.db"))
                .Where(File.Exists)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (shards.Count == 1)
                corpusPath = shards[0];
            else if (shards.Count > 1)
                Console.Error.WriteLine(
                    $"WARNING: AGENTFOLIO_CORPUS_DB unset and {shards.Count} tenant shards found — enriched stars recall DISABLED (ambiguous corpus).");
        }
    }
    if (!string.IsNullOrEmpty(corpusPath))
    {
        var corpusDb = corpusPath;
        enriched = new Slices.Stars.Retrieval.EnrichedRecallProvider(async ct =>
        {
            var store = new AgentFolio.Platform.SqliteStore(corpusDb);
            var rows = await store.GetStarsEnrichedAsync(ct).ConfigureAwait(false);
            return rows.Select(r => new Slices.Stars.Retrieval.EnrichedRepo(
                r.RepoId, r.FullName, r.Description, r.TopicsJson, r.UseFor, r.ProblemsJson)).ToList();
        });
    }
    else
    {
        Console.Error.WriteLine(
            "WARNING: stars corpus db not resolved (AGENTFOLIO_CORPUS_DB unset, no single tenant shard) — enriched recall DISABLED, GF-only retrieval.");
    }

    return new Slices.Stars.Retrieval.StarsSearchAdapter(
        new Slices.Stars.Retrieval.StarsRetrievalClient(gf),
        new Slices.Stars.Retrieval.ColbertReranker(colbert),
        enrichedRecall: enriched);
});
builder.Services.AddSingleton<AgentFolio.Contracts.IStarsSearch>(sp =>
    sp.GetRequiredService<Slices.Stars.Retrieval.StarsSearchAdapter>());

builder.Services.AddScoped<IGitHubStarsClient>(sp =>
    new HttpGitHubStarsClient(Environment.GetEnvironmentVariable("AGENTFOLIO_GITHUB_TOKEN")
        ?? throw new InvalidOperationException("AGENTFOLIO_GITHUB_TOKEN is not set."),
        sp.GetRequiredService<ILogger<HttpGitHubStarsClient>>() ?? null));
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

// Auto request logging (constitution §4-safe: no bodies — keeps SSE streaming
// unbuffered and cookies/auth headers out of logs; OTel spans carry the rest).
builder.Services.AddHttpLogging(o => o.LoggingFields =
    HttpLoggingFields.RequestMethod
    | HttpLoggingFields.RequestPath
    | HttpLoggingFields.ResponseStatusCode
    | HttpLoggingFields.Duration);

// Observability (OTel): traces + metrics + runtime + structured logs -> OTLP collector.
// OTLP_ENDPOINT e.g. http://strix:4318 (http/protobuf). Unset => loud stderr warning,
// SDK off (AGENTS.md rule 1: no silent anything — including silent observability).
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    ?? Environment.GetEnvironmentVariable("OTLP_ENDPOINT");
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("agentfolio"))
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("AgentFolio")
            .AddOtlpExporter())
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(o => o.RecordException = true)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
    builder.Logging.AddOpenTelemetry(o => o.IncludeScopes = true);
}
else
{
    Console.Error.WriteLine("WARNING: OTEL_EXPORTER_OTLP_ENDPOINT unset — OpenTelemetry DISABLED (no traces/metrics/logs export).");
}

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
// ExtractionService: same object store as uploads; extractor path from env (tests/ops pin it).
builder.Services.AddSingleton(sp => new ExtractionService(
    sp.GetRequiredService<IObjectStore>(),
    Environment.GetEnvironmentVariable(ExtractionService.ExtractorEnvVar)));
builder.Services.AddHostedService<UploadExpiryEnforcer>();

// Stars sync background jobs (#62): durable store + swappable queue seam +
// in-process worker. The sync never binds to an HTTP request lifetime.
builder.Services.AddSingleton<ISyncJobStore>(_ => new SqliteSyncJobStore(
    Path.Combine(
        Path.GetDirectoryName(Environment.GetEnvironmentVariable("AGENTFOLIO_GLOBAL_DB") ?? "agentfolio-global.db") ?? ".",
        Path.GetFileName(Environment.GetEnvironmentVariable("AGENTFOLIO_GLOBAL_DB") ?? "agentfolio-global.db") + "-sync-jobs.db")));
builder.Services.AddSingleton<ISyncJobQueue, ChannelSyncJobQueue>();
builder.Services.AddHostedService<StarsSyncWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.

// No indexing of any AgentFolio response.
app.UseHttpLogging();
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
app.MapMethods("/", ["GET", "HEAD"], () => Results.Redirect("/share/showcase", permanent: false));

// Container health probe (#100): liveness only — answers 200 as soon as the host
// is up. Dependency health is deliberate NOT here (retrieval backends degrade-open
// by contract; a ColBERT blip must not restart the container). Docker/compose
// healthchecks hit this endpoint; deep checks live in the D15 ladder (#81).
app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

app.MapChatApi();
app.MapChatCapabilitiesApi();
app.MapStarsApi();

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
