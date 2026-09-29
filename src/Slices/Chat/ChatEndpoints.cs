using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentFolio.Agents;
using AgentFolio.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using AgentFolio.Platform.Storage;
using AgentFolio.Platform.Tenancy;
namespace Slices.Chat;

/// <summary>SSE chat endpoint: streams ChatTokenEvents, then one ChatCompletionEvent + [DONE].</summary>
public static class ChatEndpoints
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat", async (HttpContext http, ChatRequest request, IServiceProvider sp, CancellationToken ct) =>
        {
            var conversationId = request.ConversationId ?? Guid.NewGuid().ToString();
            IChatClient client;
            try
            {
                client = ResolveChatClient(sp);
            }
            catch (InvalidOperationException)
            {
                // AGENTS.md rule 1: no silent fallback — name the missing dependency.
                return Results.Json(new { error = "nim_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var agent = PortfolioAgentFactory.Create(client);
            var stars = http.RequestServices.GetService<IStarsSearch>();
            IReadOnlyList<StarsSearchHit>? starHits = null;
            if (stars is not null)
            {
                try { starHits = await stars.SearchAsync(request.Message, topK: 5, ct); }
                catch { starHits = null; } // degrade-open: uncited answer beats failed chat
            }
            var tier = ChatTierSelector.Select(new ClientCapabilities(WebGPU: false),
                retrievalRequired: starHits is null || starHits.Count == 0).ToString();
            var store = http.RequestServices.GetService<IConversationStore>();

            // Persist the user turn BEFORE streaming (best-effort; never breaks the stream).
            try
            {
                if (store is not null)
                    await store.StoreChatMessageAsync(conversationId, "user", request.Message, ct);
            }
            catch { }

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            // Doc context (uploaded this session): prepend labeled, extracted texts
            // so answers reference the visitor's documents (spec 002 FR4a) AND can
            // cite them. Each doc is labeled [DOC n]; a leading `Source: <url>` line
            // in the extracted text supplies the citation URL (uploader-provenance,
            // spec 002 FR4b); the answer is instructed to cite [DOC n] markers which
            // this endpoint resolves into real citations. Cap context hard.
            var objectStore = http.RequestServices.GetService<IObjectStore>();
            string message = request.Message;
            var citations = new List<ChatCitation>();
            (string Text, string? Url)[]? ctxDocs = null;
            if (objectStore is not null)
            {
                var tidClaim = http.User.FindFirst("tid")?.Value;
                string? tid = tidClaim;
                if (string.IsNullOrEmpty(tid))
                {
                    // Doc context is BEST-EFFORT: a missing/uninitialized registry (e.g. a
                    // test host without tenancy schema) must never break chat itself.
                    try
                    {
                        var registry = http.RequestServices.GetService<TenantRegistry>();
                        tid = registry is null ? null : (await registry.GetBySlugAsync("showcase", ct))?.TenantId;
                    }
                    catch
                    {
                        tid = null;
                    }
                }
                var sid = http.Request.Cookies.TryGetValue("af_sid", out var s) && !string.IsNullOrEmpty(s) ? s : null;
                if (tid is not null && sid is not null)
                {
                    IReadOnlyList<string> keys;
                    try { keys = await objectStore.ListByPrefixAsync($"tenants/{tid}/sessions/{sid}/extracted/", ct); }
                    catch { keys = Array.Empty<string>(); } // best-effort context, never fatal

                    if (keys.Count > 0)
                    {
                        var docs = new List<(string text, string? url)>();
                        foreach (var k in keys.Take(5))
                        {
                            await using var st = await objectStore.GetAsync(k, ct);
                            if (st is null) continue;
                            using var sr = new StreamReader(st);
                            var txt = await sr.ReadToEndAsync(ct);
                            if (txt.Length > 4000) txt = txt[..4000] + "…[truncated]";
                            docs.Add((txt, ExtractSourceUrl(txt)));
                        }
                        if (docs.Count > 0)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine("Context documents from this session (cite the ones you use as [DOC 1], [DOC 2], … in your answer):");
                            for (var i = 0; i < docs.Count; i++)
                            {
                                sb.AppendLine($"--- [DOC {i + 1}] ---");
                                sb.AppendLine(docs[i].text);
                            }
                            sb.AppendLine($"---{Environment.NewLine}{Environment.NewLine}Question: {request.Message}");
                            message = sb.ToString();
                            ctxDocs = docs.ToArray();
                        }
                    }
                }
            }

            // Stars context (#66 stage D): prepend cited-candidate repos so the answer
            // references real starred repos and can cite them via [STAR n] markers,
            // resolved below into ChatCitations (same pattern as [DOC n]).
            (string FullName, string Text)[]? ctxStars = null;
            if (starHits is not null && starHits.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Relevant starred repositories (cite the ones you use as [STAR 1], [STAR 2], ... in your answer):");
                for (var i = 0; i < starHits.Count; i++)
                {
                    var h = starHits[i];
                    sb.AppendLine($"--- [STAR {i + 1}] {h.FullName} ---");
                    var text = (h.RerankText ?? h.Text);
                    if (text.Length > 600) text = text[..600] + "…[truncated]";
                    sb.AppendLine(text);
                }
                sb.AppendLine($"---{Environment.NewLine}{Environment.NewLine}Question: {request.Message}");
                message = sb.ToString();
                ctxStars = starHits.Select(h => (h.FullName, h.RerankText ?? h.Text)).ToArray();
            }

            var session = await agent.CreateSessionAsync(ct);
            var assistantText = new StringBuilder();
            await foreach (var update in agent.RunStreamingAsync(message, session, cancellationToken: ct))
            {
                if (string.IsNullOrEmpty(update.Text)) continue;
                assistantText.Append(update.Text);
                await WriteSseAsync(http.Response, JsonSerializer.Serialize(new ChatTokenEvent(CleanMarkers(update.Text)), JsonOptions), ct);
            }

            // Resolve [DOC n] markers the model cited into real ChatCitations (url +
            // title + snippet from the source document) — the constitution §2 wiring.
            if (ctxDocs is not null && ctxDocs.Length > 0)
            {
                foreach (var idx in ExtractCitedDocIndices(assistantText.ToString()))
                {
                    if (idx < 1 || idx > ctxDocs.Length) continue;
                    var (text, url) = ctxDocs[idx - 1];
                    var clean = StripSourceLine(text);
                    citations.Add(new ChatCitation(
                        url ?? "/api/session/docs",
                        $"Session document {idx}{(url is null ? "" : $" — {ShortTitle(clean)}")}",
                        Snippet(clean, 240)));
                }
            }

            if (ctxStars is not null)
            {
                var starIdx = 0;
                foreach (var idx in ExtractCitedMarkers(assistantText.ToString(), "STAR"))
                {
                    if (idx < 1 || idx > ctxStars.Length) continue;
                    var (fullName, text) = ctxStars[idx - 1];
                    starIdx++;
                    citations.Add(new ChatCitation(
                        $"https://github.com/{fullName}",
                        fullName,
                        Snippet(text, 240)));
                }
                _ = starIdx; // ordered by citation appearance below via list order
            }

            var completion = new ChatCompletionEvent(conversationId, tier, citations);
            await WriteSseAsync(http.Response, JsonSerializer.Serialize(completion, JsonOptions), ct);
            await WriteSseAsync(http.Response, "[DONE]", ct);

            // Persist the assistant turn AFTER the stream completes (best-effort).
            try
            {
                if (store is not null && assistantText.Length > 0)
                    await store.StoreChatMessageAsync(conversationId, "assistant", assistantText.ToString(), ct);
            }
            catch { }

            return Results.Empty;
        })
        .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Keyed 'nim' client from DI. NO FALLBACK (AGENTS.md rule 1): when NIM is not
    /// configured the dependency fails loudly — chat returns 503 nim_not_configured,
    /// never an echo. Tests inject their fake via keyed DI override.
    /// </summary>
    internal static IChatClient ResolveChatClient(IServiceProvider sp)
        => sp.GetRequiredKeyedService<IChatClient>(NimChatClientFactory.ClientName);

    /// <summary>Regex for [DOC n] markers the model may cite (case-insensitive).</summary>
    private static readonly System.Text.RegularExpressions.Regex DocMarkerRegex =
        new(@"\[DOC\s+(\d+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Distinct, ordered doc indices the answer cited via [DOC n] markers.</summary>
    internal static IEnumerable<int> ExtractCitedDocIndices(string answer)
    {
        var seen = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in DocMarkerRegex.Matches(answer))
        {
            if (int.TryParse(m.Groups[1].Value, out var idx) && seen.Add(idx))
                yield return idx;
        }
    }

    /// <summary>Regex for [STAR n] markers (case-insensitive).</summary>
    private static readonly System.Text.RegularExpressions.Regex StarMarkerRegex =
        new(@"\[STAR\s+(\d+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Distinct, ordered indices cited via the given marker kind (e.g. "STAR").</summary>
    internal static IEnumerable<int> ExtractCitedMarkers(string answer, string kind)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            $@"\[{System.Text.RegularExpressions.Regex.Escape(kind)}\s+(\d+)\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        var seen = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(answer))
            if (int.TryParse(m.Groups[1].Value, out var idx) && seen.Add(idx))
                yield return idx;
    }

    /// <summary>Strips [DOC n] and [STAR n] markers from a token before streaming.</summary>
    internal static string CleanMarkers(string token)
        => StarMarkerRegex.Replace(DocMarkerRegex.Replace(token, string.Empty), string.Empty);

    /// <summary>Pulls a `Source: http…` line from extracted doc text (uploader provenance).</summary>
    internal static string? ExtractSourceUrl(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
            {
                var url = t["Source:".Length..].Trim();
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    return url;
            }
            if (t.Length > 0 && !t.StartsWith("---", StringComparison.Ordinal)) break; // only leading Source lines count
        }
        return null;
    }

    /// <summary>Removes the leading Source line for snippet display.</summary>
    internal static string StripSourceLine(string text)
    {
        var lines = text.Split('\n');
        return lines.Length > 0 && lines[0].TrimStart().StartsWith("Source:", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\n', lines[1..]).TrimStart()
            : text;
    }

    /// <summary>First non-empty line capped at 60 chars, for the citation title.</summary>
    internal static string ShortTitle(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            return t.Length <= 60 ? t : t[..57] + "…";
        }
        return "document";
    }

    /// <summary>Single-line snippet capped at <paramref name="max"/> chars.</summary>
    internal static string Snippet(string text, int max)
    {
        var single = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }

    private static async Task WriteSseAsync(HttpResponse response, string payload, CancellationToken ct)
    {
        await response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }

}
