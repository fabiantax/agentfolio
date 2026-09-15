using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentFolio.Slices.Stars;

namespace AgentFolio.UnitTests;

/// <summary>Fake IGitHubStarsClient for test scenarios.</summary>
public sealed class MockGitHubStarsClient : IGitHubStarsClient
{
    private List<StarredRepoDto> _pages;
    private long _resetUnix; // for rate-limit faking
    private bool _shouldDelay;

    public MockGitHubStarsClient()
    {
        _pages = new();
        _resetUnix = 0;
        _shouldDelay = false;
    }

    public void AddPage(IReadOnlyList<StarredRepoDto> page)
    {
        _pages.AddRange(page);
    }

    public void AddPageWithRateLimit(IReadOnlyList<StarredRepoDto> page, long resetOffsetSeconds)
    {
        AddPage(page);
        _resetUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + resetOffsetSeconds;
        _shouldDelay = resetOffsetSeconds < 0;
    }

    public Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(CancellationToken ct)
    {
        // Simulate pagination across pages
        var all = new List<StarredRepoDto>();

        for (int i = 0; i < _pages.Count; i++)
        {
            all.AddRange(_pages[i]);
            // Simulate Link header pagination - last page has no next
            if (i < _pages.Count - 1) continue;
            break;
        }

        // Simulate rate-limit delay if configured
        if (_shouldDelay)
        {
            var waitSeconds = Math.Max(1, -_resetUnix + 1);
            var delay = System.TimeSpan.FromSeconds(waitSeconds);
            // Note: in real test this would await, but for sync simplicity we just note it
        }

        return Task.FromResult<IReadOnlyList<StarredRepoDto>>(all.AsReadOnly());
    }
}