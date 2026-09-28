namespace Slices.Stars;

using Microsoft.Extensions.Logging;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Fetches GitHub starred repos via raw HttpClient with pagination and rate-limit backoff.</summary>
public class HttpGitHubStarsClient : IGitHubStarsClient
{
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public HttpGitHubStarsClient(string token, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _token = token;
        _logger = logger;
        // #67 stall-hardening (measured 2026-09-28: a dead-idle TLS read sat 1h45m while
        // HttpClient.Timeout never fired on the stalled body read):
        //  - SocketsHttpHandler with PooledConnectionLifetime: pooled connections are
        //    recycled, so a half-dead keep-alive socket cannot be reused forever.
        //  - ConnectTimeout bounds the TCP/TLS handshake independently of request timeout.
        //  - Per-page wall-clock budget (linked CTS, 120 s) bounds the WHOLE page attempt
        //    including the body read — a stall becomes TaskCanceledException and flows
        //    into the existing bounded retry instead of hanging forever.
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(PageBudgetSeconds + 30), // outer safety net above the per-page budget
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentFolio/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.star+json");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    internal const int PageBudgetSeconds = 120;

    public Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(CancellationToken ct) =>
        GetAllStarredAsync(ct, onProgress: null);

    public async Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(
        CancellationToken ct, Action<int, string>? onProgress)
    {
        var all = new List<StarredRepoDto>();
        var pagesDone = 0;
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
                    using var pageCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pageCts.CancelAfter(TimeSpan.FromSeconds(PageBudgetSeconds));
                    using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                    _logger?.LogInformation("stars fetch: page {Page} attempt {Attempt} from {Url}",
                        pagesDone + 1, pageAttempt + 1, nextUrl);
                    using var resp = await SendPageAsync(request, pageCts.Token).ConfigureAwait(false);

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

                        _logger?.LogWarning("stars fetch: page {Page} rate-limited ({Status}); waiting {Delay}s",
                            pagesDone + 1, (int)resp.StatusCode, delay.TotalSeconds);
                        try { onProgress?.Invoke(pagesDone, $"page {pagesDone + 1} rate-limited, waiting {delay.TotalSeconds:F0}s"); } catch { }
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        continue; // same page
                    }

                    resp.EnsureSuccessStatusCode();
                    body = await resp.Content.ReadAsStringAsync(pageCts.Token).ConfigureAwait(false);
                    resp.Headers.TryGetValues("Link", out var linkValues);
                    linkHeader = linkValues?.FirstOrDefault();
                    pagesDone++;
                    _logger?.LogInformation("stars fetch: page {Page} ok ({Bytes} bytes)", pagesDone, body.Length);
                    try { onProgress?.Invoke(pagesDone, $"page {pagesDone} ok ({body.Length} bytes)"); } catch { /* progress must never break the fetch */ }
                }
                catch (Exception e) when (
                    !ct.IsCancellationRequested &&
                    e is HttpRequestException or TaskCanceledException or IOException)
                {
                    pageAttempt++;
                    if (pageAttempt > 5)
                        throw; // fail loudly — never a silent partial fetch
                    var waitSecs = Math.Min(10 * pageAttempt, 60);
                    _logger?.LogWarning("stars fetch: page {Page} attempt {Attempt} failed ({Err}); retrying in {Wait}s",
                        pagesDone + 1, pageAttempt, e.GetType().Name, waitSecs);
                    try { onProgress?.Invoke(pagesDone, $"page {pagesDone + 1} retry {pageAttempt} after {e.GetType().Name}"); } catch { }
                    await Task.Delay(TimeSpan.FromSeconds(waitSecs), ct).ConfigureAwait(false);
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

    /// <summary>Test seam: the actual HTTP send for one page attempt.</summary>
    internal virtual Task<HttpResponseMessage> SendPageAsync(HttpRequestMessage request, CancellationToken ct) =>
        _http.SendAsync(request, ct);

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