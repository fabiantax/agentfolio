using System;

namespace AgentFolio.Slices.Saves;

/// <summary>
/// Canonicalizes a post URL by:
/// - Lowercasing the host
/// - Keeping the https scheme
/// - Keeping the absolute path
/// - Dropping ALL query parameters
/// - Stripping trailing slash
/// - Extracting activity IDs from feed/update/urn:li: paths
/// </summary>
public sealed class SaveCanonicalizer
{
    /// <summary>
    /// Canonicalizes a post URL.
    /// Returns null for null or invalid input.
    /// </summary>
    /// <param name="postUrl">The URL to canonicalize.</param>
    /// <returns>The canonicalized URL, or null if input is null/invalid.</returns>
    public static string? Canonicalize(string? postUrl)
    {
        if (string.IsNullOrWhiteSpace(postUrl))
            return null;

        if (!Uri.TryCreate(postUrl, UriKind.Absolute, out var uri))
            return null;

        // Must have https scheme
        if (uri.Scheme != "https")
            return null;

        // Lowercase the host
        var lowerHost = uri.Host.ToLowerInvariant();

        // Build the canonical URL: scheme + https:// + lowercased host + absolute path
        // Drop ALL query parameters
        // Strip trailing slash from path
        var path = uri.AbsolutePath;
        if (path.Length > 1 && path.EndsWith("/"))
            path = path[..^1];

        // Reconstruct: https://lowerhost/path
        var canonical = $"{uri.Scheme}://{lowerHost}{path}";

        // Extract activity ID from feed/update/urn:li: paths
        // Patterns: /activities/([^/]+) or activity-(\d+)
        canonical = ExtractActivityId(canonical, uri);

        return canonical;
    }

    private static string ExtractActivityId(string canonical, Uri originalUri)
    {
        // Check the original URL path for activity ID patterns before canonicalization
        // We need to examine the original path since canonical may have stripped query params
        var originalPath = originalUri.AbsolutePath;

        // Pattern: /activities/([^/]+)
        var activitiesMatch = System.Text.RegularExpressions.Regex.Match(originalPath, @"/activities/([^/]+)");
        if (activitiesMatch.Success)
        {
            var activityId = activitiesMatch.Groups[1].Value;
            // Replace the path with just the activity ID
            return $"{originalUri.Scheme}://{originalUri.Host}/activities/{activityId}";
        }

        // Pattern: activity-(\d+)
        var activityNumMatch = System.Text.RegularExpressions.Regex.Match(originalPath, @"activity-(\d+)");
        if (activityNumMatch.Success)
        {
            var activityId = activityNumMatch.Groups[1].Value;
            return $"{originalUri.Scheme}://{originalUri.Host}/activities/{activityId}";
        }

        return canonical;
    }
}