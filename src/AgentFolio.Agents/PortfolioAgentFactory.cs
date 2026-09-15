using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFolio.Agents;

/// <summary>
/// Builds the portfolio assistant. Tools are INJECTED (port pattern) — this factory
/// knows nothing about retrieval internals, only that some tools may be provided.
/// </summary>
public static class PortfolioAgentFactory
{
    public const string AgentName = "PortfolioAssistant";

    public const string Instructions =
        "You are the AgentFolio portfolio assistant. " +
        "Cite source url for every factual claim; if retrieval returned nothing, say so. " +
        "Never invent sources, numbers, or holdings.";

    /// <summary>Creates the agent over the supplied chat client and injected tools.</summary>
    public static AIAgent Create(IChatClient client, IReadOnlyList<AIFunction>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        // MAF 1.21: IChatClient.AsAIAgent(instructions, name, description, tools).
        return client.AsAIAgent(
            instructions: Instructions,
            name: AgentName,
            description: "AgentFolio portfolio assistant with mandatory source citations.",
            tools: tools is { Count: > 0 } ? [.. tools] : null);
    }
}
