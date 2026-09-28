using System.Net;
using Slices.Stars;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>
/// #67: per-page progress visibility + stall-hardened fetch.
/// </summary>
public class StarsSyncProgressTests
{
    [Fact]
    public void ReportPageProgress_is_legal_only_while_running()
    {
        var job = StarsSyncJob.Queued("t1");
        Assert.Throws<InvalidOperationException>(() => job.ReportPageProgress(1, "page 1 ok"));

        job.MarkRunning();
        job.ReportPageProgress(3, "page 3 ok (660 KB)");
        Assert.Equal(3, job.PagesDone);
        Assert.Contains("page 3", job.LastPageNote);
    }

    [Fact]
    public async Task Stalled_read_fails_within_page_budget_and_retries()
    {
        // Attempt 1: the send HANGS until the page CTS fires (a dead-idle TLS read).
        // Attempt 2: succeeds. The client must recover via the bounded retry without
        // waiting on any outer 10-minute timeout.
        var attempts = 0;
        var client = new StallThenOkClient((_, _) =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n == 1)
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("stalled read"));
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };
            if (n == 2) // exactly one next page, then terminal
                resp.Headers.Add("Link", "<https://api.github.com/user/starred?page=2>; rel=\"next\"");
            return Task.FromResult(resp);
        });

        var progress = new List<string>();
        var all = await client.GetAllStarredAsync(CancellationToken.None,
            (pages, note) => progress.Add($"{pages}:{note}"));

        Assert.Equal(3, attempts); // stall, page-1 success, page-2 success
        Assert.Empty(all);         // empty page is still a completed fetch
        Assert.True(progress.Count >= 1);
    }

    [Fact]
    public async Task Progress_callback_receives_per_page_events()
    {
        var okPage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        };
        okPage.Headers.Add("Link", "<https://api.github.com/user/starred?page=2>; rel=\"next\"");
        var page2 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        };
        var pages = new Queue<HttpResponseMessage>(new[] { okPage, page2 });

        var client = new ScriptedClient(_ => Task.FromResult(pages.Dequeue()));
        var seen = new List<int>();
        await client.GetAllStarredAsync(CancellationToken.None, (p, _) => seen.Add(p));

        Assert.Equal(new[] { 1, 2 }, seen);
    }

    [Fact]
    public async Task Rate_limited_page_reports_waiting_note()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
        limited.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        };
        var pages = new Queue<HttpResponseMessage>(new[] { limited, ok });

        var client = new ScriptedClient(_ => Task.FromResult(pages.Dequeue()));
        var notes = new List<string>();
        await client.GetAllStarredAsync(CancellationToken.None, (_, n) => notes.Add(n));

        Assert.Contains(notes, n => n.Contains("rate-limited"));
    }

    /// <summary>Client whose transport is fully scripted (no real HTTP).</summary>
    private class ScriptedClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpGitHubStarsClient("t", null)
    {
        internal override Task<HttpResponseMessage> SendPageAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request);
    }

    /// <summary>Client whose first send stalls (the measured 1h45m wedge).</summary>
    private sealed class StallThenOkClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : ScriptedClient(_ => send(_, default));
}
