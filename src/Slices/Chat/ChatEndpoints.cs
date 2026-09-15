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
            var client = ResolveChatClient(sp);
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

            var session = await agent.CreateSessionAsync(ct);
            var assistantText = new StringBuilder();
            await foreach (var update in agent.RunStreamingAsync(request.Message, session, cancellationToken: ct))
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
        })
        .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Keyed 'nim' client from DI; falls back to an inline echo client when NIM env is
    /// unset so the API runs (and tests run) keyless.
    /// </summary>
    internal static IChatClient ResolveChatClient(IServiceProvider sp)
    {
        try
        {
            return sp.GetRequiredKeyedService<IChatClient>(NimChatClientFactory.ClientName);
        }
        catch (Exception)
        {
            return new InlineDelegateChatClient();
        }
    }

    private static async Task WriteSseAsync(HttpResponse response, string payload, CancellationToken ct)
    {
        await response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>FakeChatClient-equivalent: streams the completion in two chunks, keyless fallback.</summary>
    internal sealed class InlineDelegateChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = string.Join(" ", messages.SelectMany(m => m.Contents).OfType<TextContent>().Select(c => c.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"[echo] {text}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in new[] { "[echo] ", "hello" })
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
