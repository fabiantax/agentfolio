namespace Slices.Stars;

/// <summary>DTO for a GitHub starred repository.</summary>
public sealed record StarredRepoDto(
    long RepoId,
    string Name,
    string FullName,
    string? Description,
    string HtmlUrl,
    int Stars,
    string? Language,
    IReadOnlyList<string> Topics,
    string? Owner,
    string StarredAtIso);