using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace AgentFolio.Agents;

/// <summary>
/// Builds an <see cref="IChatClient"/> against an NVIDIA NIM (OpenAI-compatible) endpoint
/// from environment configuration, using the canonical Microsoft Agent Framework wiring.
/// </summary>
public static class NimChatClientFactory
{
    public const string ClientName = "nim";

    public static string BaseUrlEnv => "NIM_BASE_URL";
    public static string ApiKeyEnv => "NIM_API_KEY";
    public static string ModelEnv => "NIM_MODEL";
    public static string DefaultModel => "meta/llama-3.1-8b-instruct";

    /// <summary>Canonical MAF wiring: OpenAIClient → GetChatClient(model).AsIChatClient().</summary>
    public static IChatClient Create(string baseUrl, string apiKey, string? model = null)
    {
        var opts = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        IChatClient inner = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), opts)
            .GetChatClient(model ?? Environment.GetEnvironmentVariable(ModelEnv) ?? DefaultModel)
            .AsIChatClient();
        return inner;
    }

    /// <summary>Builds a client from NIM_BASE_URL / NIM_API_KEY / NIM_MODEL environment variables.</summary>
    public static IChatClient CreateFromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlEnv)
            ?? throw new InvalidOperationException($"{BaseUrlEnv} is not set.");
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnv)
            ?? throw new InvalidOperationException($"{ApiKeyEnv} is not set.");
        return Create(baseUrl, apiKey);
    }

    /// <summary>Registers the 'nim' <see cref="IChatClient"/> in DI by key.</summary>
    public static IServiceCollection AddNimChatClient(this IServiceCollection services, string? baseUrl = null, string? apiKey = null)
    {
        services.AddKeyedSingleton<IChatClient>(
            ClientName,
            (sp, _) => Create(
                baseUrl ?? Environment.GetEnvironmentVariable(BaseUrlEnv)
                    ?? throw new InvalidOperationException($"{BaseUrlEnv} is not set."),
                apiKey ?? Environment.GetEnvironmentVariable(ApiKeyEnv)
                    ?? throw new InvalidOperationException($"{ApiKeyEnv} is not set.")));
        return services;
    }
}
