namespace Slices.Chat;

/// <summary>GET /chat/capabilities response: what inference surfaces exist right now.</summary>
/// <param name="Nim">True when the server-side NIM chat client is configured and resolvable.</param>
/// <param name="BrowserInference">Static capability: the chat widget ships a WebGPU browser tier.</param>
public sealed record ChatCapabilities(bool Nim, bool BrowserInference);
