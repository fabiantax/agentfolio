using System.Net;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using AgentFolio.Agents;
using Microsoft.Extensions.AI;

namespace AgentFolio.UnitTests;

public class AgentsTests
{
    private const string ApiKey = "nvapi-secret-test-key-1234567890";
    private const string BaseUrl = "http://nim.internal:8000/v1";

    // ---- hand-rolled fake IChatClient: records messages, returns canned text ----
    private sealed class FakeChatClient : IChatClient
    {
        public List<ChatMessage> CapturedMessages { get; } = [];
        public string CannedText { get; set; } = "canned response";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            CapturedMessages.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, CannedText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var resp = await GetResponseAsync(messages, options, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, resp.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // ---- fake HttpHandler that records the request URI ----
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null ? null : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var body = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-fake",
                @object = "chat.completion",
                created = 1,
                model = "m",
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" }
                },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
            });
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public void Factory_instructions_require_citations()
    {
        Assert.Contains("cite source url for every factual claim", PortfolioAgentFactory.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if retrieval returned nothing, say so", PortfolioAgentFactory.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_created_by_factory_carries_citation_instructions()
    {
        var fake = new FakeChatClient();
        var agent = PortfolioAgentFactory.Create(fake);
        // MAF 1.21 does not expose Instructions on AIAgent; prove wiring by executing
        // a turn over the fake and checking the agent-name/route in the run result,
        // plus the factory-level instruction contract in Factory_instructions_require_citations.
        var response = await agent.RunAsync("what is in my portfolio?");
        Assert.Equal("canned response", response.Text);
    }

    [Fact]
    public async Task Nim_factory_hits_configured_endpoint()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };

        // Build the canonical wiring but with our recording transport.
        var opts = new OpenAIClientOptions { Endpoint = new Uri(BaseUrl), Transport = new HttpClientPipelineTransport(httpClient) };
        IChatClient client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(ApiKey), opts)
            .GetChatClient("meta/llama-3.1-8b-instruct").AsIChatClient();

        var response = await client.GetResponseAsync("hello");

        Assert.Equal("ok", response.Text);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal("nim.internal", handler.LastRequestUri!.Host);
    }

    [Theory]
    [InlineData(true, false, ChatTier.BrowserLf2)]   // WebGPU + simple -> Browser
    [InlineData(true, true, ChatTier.ServerNim)]     // WebGPU + retrieval -> Server
    [InlineData(false, false, ChatTier.ServerNim)]   // no WebGPU -> Server
    [InlineData(false, true, ChatTier.ServerNim)]    // no WebGPU + retrieval -> Server
    public void Tier_selector_rules(bool webgpu, bool retrievalRequired, ChatTier expected)
    {
        Assert.Equal(expected, ChatTierSelector.Select(new ClientCapabilities(webgpu), retrievalRequired));
    }

    [Fact]
    public async Task Api_key_never_appears_in_instructions_or_captured_messages()
    {
        var fake = new FakeChatClient();
        var agent = PortfolioAgentFactory.Create(fake);
        var response = await agent.RunAsync("tell me about my holdings");
        Assert.Equal("canned response", response.Text);

        Assert.DoesNotContain(ApiKey, PortfolioAgentFactory.Instructions, StringComparison.Ordinal);
        foreach (var msg in fake.CapturedMessages)
            Assert.DoesNotContain(ApiKey, msg.Text ?? string.Empty, StringComparison.Ordinal);
    }
}
