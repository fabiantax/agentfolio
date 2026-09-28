namespace Slices.Stars;

/// <summary>Interface for fetching GitHub starred repositories via HTTP.</summary>
public interface IGitHubStarsClient
{
    /// <summary>Returns all starred repos across all paginated pages.</summary>
    Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(CancellationToken ct);

    /// <summary>
    /// Same fetch, with per-page progress reporting (#67): invoked after EVERY page
    /// attempt (success or retry) with pagesCompleted and a short note. Never throws
    /// into the fetch loop.
    /// </summary>
    Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(
        CancellationToken ct,
        Action<int, string>? onProgress);
}
