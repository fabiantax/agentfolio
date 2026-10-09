namespace AgentFolio.Api;

/// <summary>
/// Production startup contract (#100): in Production the container MUST have the
/// full runtime env or it fails LOUDLY at startup — no lazy first-request throws,
/// no silent localhost defaults (AGENTS.md rule 1). Dev/Test is unaffected: every
/// check is gated on IsProduction, so local runs and the E2E factory keep working.
///
/// What is required in Production and why:
/// - NIM_BASE_URL / NIM_API_KEY      chat completions (lazy IChatClient throw would
///                                   otherwise surface only on the first /chat call).
/// - AGENTFOLIO_GITHUB_TOKEN         stars sync client (Program.cs already throws,
///                                   but lazily — re-checked here at t=0).
/// - AGENTFOLIO_GLOBAL_DB            tenant registry location; the container gets an
///                                   explicit path instead of a cwd-relative accident.
/// - AGENTFOLIO_S3_ENDPOINT (+bucket/keys)  uploads: Program.cs already fails loudly
///                                   in Production when unset — kept as the single
///                                   source of truth, not duplicated here.
/// - GF_A2A_URL / COLBERT_URL        retrieval backends. These have code-level
///                                   localhost defaults that are WRONG inside a
///                                   container (127.0.0.1 in the container is the
///                                   container, not strix) — in Production an unset
///                                   value would silently answer "no citations".
///                                   Required, never defaulted, in Production.
/// Deliberately NOT required: NIM_MODEL (has a sane baked default), GLINER_URL /
/// RELEX_URL (extraction stage degrades per-repo by design; the preview tenant
/// ships a frozen corpus and never runs ingest), OTEL (warns, by design).
/// </summary>
public static class ProductionStartupValidator
{
    /// <summary>Environment-variable names the API requires when running in Production.</summary>
    public static readonly string[] RequiredVariables =
    [
        "NIM_BASE_URL",
        "NIM_API_KEY",
        "AGENTFOLIO_GITHUB_TOKEN",
        "AGENTFOLIO_GLOBAL_DB",
        "GF_A2A_URL",
        "COLBERT_URL",
    ];

    /// <summary>
    /// Throws a named, actionable error listing EVERY missing variable when the
    /// app runs in Production with incomplete env. No-op outside Production.
    /// </summary>
    public static void Validate(bool isProduction)
    {
        if (!isProduction)
        {
            return;
        }

        var missing = RequiredVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Production startup failed — required environment variables are not set: "
                + string.Join(", ", missing)
                + " (fail-loud per AGENTS.md rule 1; set them in the container env, never as build args).");
        }
    }
}
