namespace AgentFolio.Agents;

/// <summary>Browser capabilities reported by the client.</summary>
public readonly record struct ClientCapabilities(bool WebGPU);

/// <summary>
/// Pure tier-selection logic: browser inference only when the browser can run it
/// AND the turn needs no server-side retrieval.
/// </summary>
public static class ChatTierSelector
{
    public static ChatTier Select(ClientCapabilities caps, bool retrievalRequired) =>
        caps.WebGPU && !retrievalRequired ? ChatTier.BrowserLf2 : ChatTier.ServerNim;
}
