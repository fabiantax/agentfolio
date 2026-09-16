using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentFolio.Agents;
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
            // Retrieval is not wired yet; server-side turns are retrievalRequired until it lands.
            var tier = ChatTierSelector.Select(new ClientCapabilities(WebGPU: false), retrievalRequired: true).ToString();
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

            // Doc context (uploaded this session): prepend extracted texts so answers
            // reference the visitor's documents (spec 002 FR4a). Cap context hard.
            var objectStore = http.RequestServices.GetService<IObjectStore>();
            string message = request.Message;
            if (objectStore is not null)
            {
                var tidClaim = http.User.FindFirst("tid")?.Value;
                string? tid = tidClaim;
                if (string.IsNullOrEmpty(tid))
                {
                    var registry = http.RequestServices.GetService<TenantRegistry>();
                    tid = registry is null ? null : (await registry.GetBySlugAsync("showcase", ct))?.TenantId;
                }
                var sid = http.Request.Cookies.TryGetValue("af_sid", out var s) && !string.IsNullOrEmpty(s) ? s : null;
                if (tid is not null && sid is not null)
                {
                    var keys = await objectStore.ListByPrefixAsync($"tenants/{tid}/sessions/{sid}/extracted/", ct);
                    if (keys.Count > 0)
                    {
                        var docs = new List<string>();
                        foreach (var k in keys.Take(5))
                        {
                            await using var st = await objectStore.GetAsync(k, ct);
                            if (st is null) continue;
                            using var sr = new StreamReader(st);
                            var txt = await sr.ReadToEndAsync(ct);
                            if (txt.Length > 4000) txt = txt[..4000] + "…[truncated]";
                            docs.Add(txt);
                        }
                        if (docs.Count > 0)
                            message = $"Context documents from this session:\n---\n{string.Join("\n---\n", docs)}\n---\n\nQuestion: {request.Message}";
                    }
                }
            }

            var session = await agent.CreateSessionAsync(ct);
            var assistantText = new StringBuilder();
            await foreach (var update in agent.RunStreamingAsync(message, session, cancellationToken: ct))
            {
                if (string.IsNullOrEmpty(update.Text)) continue;
                assistantText.Append(update.Text);
                await WriteSseAsync(http.Response, JsonSerializer.Serialize(new ChatTokenEvent(update.Text), JsonOptions), ct);
            }

            // Citations empty for now — retrieval wiring lands in a later slice.
            var completion = new ChatCompletionEvent(conversationId, tier, []);
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

    private static async Task WriteSseAsync(HttpResponse response, string payload, CancellationToken ct)
    {
        await response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }

}
