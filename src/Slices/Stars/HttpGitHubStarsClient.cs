namespace Slices.Stars;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Fetches GitHub starred repos via raw HttpClient with pagination and rate-limit backoff.</summary>
public sealed class HttpGitHubStarsClient : IGitHubStarsClient
{
    private readonly string _token;
    private readonly HttpClient _http;

    public HttpGitHubStarsClient(string token)
    {
        _token = token;
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentFolio/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.star+json");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(CancellationToken ct)
    {
        var all = new List<StarredRepoDto>();
        string? nextUrl = "https://api.github.com/user/starred?per_page=100";

        while (nextUrl != null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                (int)response.StatusCode == 429)
            {
                var resetStr = response.Headers?.GetValues("X-RateLimit-Reset")?.FirstOrDefault();
                if (long.TryParse(resetStr, out var resetUnix))
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var waitSecs = Math.Max(1, (resetUnix - now) + 1);
                    var maxWait = 600; // 10 min cap
                    if (waitSecs > maxWait) waitSecs = maxWait;
                    await Task.Delay(TimeSpan.FromSeconds(waitSecs), ct).ConfigureAwait(false);
                }
                // Retry the same URL
                continue;
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            all.AddRange(ParseStarredPage(body));

            // Parse Link header for pagination
            nextUrl = null;
            if (response.Headers.TryGetValues("Link", out var linkValues))
            {
                foreach (var link in linkValues!)
                {
                    // Format: "<url>; rel=\"next\""
                    var idx = link.IndexOf("; rel=\"next\"");
                    if (idx >= 0)
                    {
                        var url = link.Substring(1, idx - 1);
                        nextUrl = url;
                    }
                }
            }
        }

        return all;
    }

    /// <summary>
    /// Parses one page of GET /user/starred with Accept: application/vnd.github.star+json.
    /// Live shape (measured 2026-09-17 against api.github.com): items are
    /// { "starred_at": ..., "repo": {...} } — the wrapper key is "repo"; the
    /// documented "repository" variant is accepted too. REST v3 "topics" is a
    /// plain string array; the GraphQL-style { nodes: [...] } object is accepted
    /// for robustness.
    /// </summary>
    public static IReadOnlyList<StarredRepoDto> ParseStarredPage(string jsonBody)
    {
        using var doc = JsonDocument.Parse(jsonBody);
        var result = new List<StarredRepoDto>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var repoItem = item.TryGetProperty("repository", out var repoProp)
                ? repoProp
                : item.GetProperty("repo");
            var topics = new List<string>();
            var topicsObj = repoItem.GetProperty("topics");
            if (topicsObj.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in topicsObj.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String) topics.Add(t.GetString()!);
            }
            else if (topicsObj.ValueKind == JsonValueKind.Object && topicsObj.TryGetProperty("nodes", out var nodes))
            {
                foreach (var node in nodes.EnumerateArray())
                    topics.Add(node.GetString()!);
            }

            result.Add(new StarredRepoDto(
                repoItem.GetProperty("id").GetInt64(),
                repoItem.GetProperty("name").GetString()!,
                GetStringNull(repoItem, "description"),
                repoItem.GetProperty("html_url").GetString()!,
                repoItem.GetProperty("stargazers_count").GetInt32(),
                GetStringNull(repoItem, "language"),
                topics,
                repoItem.GetProperty("owner").GetProperty("login").GetString()!,
                item.GetProperty("starred_at").GetString()!));
        }
        return result;
    }

    private static string? GetStringNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
            return prop.ValueKind == System.Text.Json.JsonValueKind.Null ? null : prop.GetString();
        return null;
    }
}