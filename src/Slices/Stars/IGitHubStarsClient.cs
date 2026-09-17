namespace Slices.Stars;

/// <summary>Interface for fetching GitHub starred repositories via HTTP.</summary>
public interface IGitHubStarsClient
{
    /// <summary>Returns all starred repos across all paginated pages.</summary>
    Task<IReadOnlyList<StarredRepoDto>> GetAllStarredAsync(CancellationToken ct);
}