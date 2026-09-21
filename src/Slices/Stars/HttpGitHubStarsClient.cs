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
        // Measured 2026-09-20: a single starred page (100 items, ~660 KB gz) can take
        // >100 s on GitHub's side; the HttpClient default of 100 s aborted a 46-minute
        // full sync mid-pagination (TaskCanceledException). Pages may be slow; they are
        // never silently wrong — so allow a long single-page ceiling and rely on the
        // bounded per-page retries below for genuine stalls.
        _http.Timeout = TimeSpan.FromMinutes(10);
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
            // Per-page fetch with bounded retry on ANY transient failure.
            // Measured live 2026-09-20: a 1000+ page fetch over ~45 min WILL hit
            // transient transport errors, and each one discarded the ENTIRE fetch:
            //   run 1 — HttpClient.Timeout (100 s default) killed page N at 46 min
            //   run 2 — HttpIOException "response ended prematurely" at ~20 min
            // Retry the SAME page on any of: timeout, connection reset/truncation,
            // HTTP-level failure — bounded, with escalating backoff.
            var pageAttempt = 0;
            string? body = null;
            string? linkHeader = null;
            while (body is null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                    using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);

                    if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                        (int)resp.StatusCode == 429)
                    {
                        // Rate limit / secondary limit backoff. Measured live 2026-09-17:
                        // a 403 WITHOUT X-RateLimit-Reset used to `continue` with NO delay —
                        // an infinite hot retry loop that never completes and never counts
                        // against the core quota (403s are unmetered), so it is invisible
                        // in /rate_limit. Always wait, honor Retry-After / reset headers
                        // when present, and give up loudly after a bounded number of tries.
                        pageAttempt++;
                        if (pageAttempt > 5)
                            throw new InvalidOperationException(
                                $"github_rate_limited: {nextUrl} still {(int)resp.StatusCode} after {pageAttempt - 1} backoff retries");

                        var delay = TimeSpan.FromSeconds(5);
                        var resetStr = resp.Headers?.GetValues("X-RateLimit-Reset")?.FirstOrDefault();
                        if (long.TryParse(resetStr, out var resetUnix))
                        {
                            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var waitSecs = Math.Max(5, (resetUnix - now) + 1);
                            delay = TimeSpan.FromSeconds(Math.Min(waitSecs, 120)); // cap 2 min per wait
                        }
                        if (resp.Headers?.RetryAfter?.Delta is { } delta)
                            delay = TimeSpan.FromSeconds(Math.Max(delay.TotalSeconds, Math.Min(delta.TotalSeconds, 120)));

                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        continue; // same page
                    }

                    resp.EnsureSuccessStatusCode();
                    body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    resp.Headers.TryGetValues("Link", out var linkValues);
                    linkHeader = linkValues?.FirstOrDefault();
                }
                catch (Exception e) when (
                    !ct.IsCancellationRequested &&
                    e is HttpRequestException or TaskCanceledException or IOException)
                {
                    pageAttempt++;
                    if (pageAttempt > 5)
                        throw; // fail loudly — never a silent partial fetch
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10 * pageAttempt, 60)), ct).ConfigureAwait(false);
                    // same page; body stays null
                }
            }

            all.AddRange(ParseStarredPage(body));

            // Pace pagination: rapid back-to-back page fetches trip GitHub's
            // secondary rate limits (measured: ~30 pages in, then 403 storm).
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);

            // Parse Link header for pagination
            nextUrl = null;
            if (linkHeader is not null)
            {
                // Format: "<url>; rel=\"next\""
                var idx = linkHeader.IndexOf("; rel=\"next\"");
                if (idx >= 0)
                {
                    nextUrl = linkHeader.Substring(1, idx - 1);
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
                repoItem.GetProperty("full_name").GetString()!,
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