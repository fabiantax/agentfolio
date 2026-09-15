namespace AgentFolio.Agents;

/// <summary>Where a chat turn executes.</summary>
public enum ChatTier
{
    /// <summary>Browser-resident LlamaF.2 (WebGPU).</summary>
    BrowserLf2,

    /// <summary>Server-side NVIDIA NIM inference.</summary>
    ServerNim,
}
