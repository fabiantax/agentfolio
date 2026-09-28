namespace Slices.Stars.Graph;

/// <summary>
/// Ships stars-graph Cypher to GraphFusion over gf-a2a HTTP (tailscale :50052).
/// The A2A endpoint is POST-RPC (GET / = 405, measured); the wire shape lives
/// behind SendCypherAsync so stage C can swap transport without touching callers.
/// Idempotency: every statement is MERGE-based — replaying is safe.
/// </summary>
public sealed class GraphFusionClient(string a2aBaseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(a2aBaseUrl), Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Execute one Cypher statement (MERGE-upsert). Throws on non-success.</summary>
    public async Task ExecuteAsync(string cypher, CancellationToken ct = default)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { query = cypher });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Upsert one repo's whole chunk set in order (repo -> chunks -> next links).</summary>
    }
