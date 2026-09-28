using Slices.Stars;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>
/// Regression for the 111-page infinite loop (prod, 2026-09-28): the Link-header
/// parser kept the closing '&gt;' — "page=2&gt;" — GitHub served page 1 forever.
/// </summary>
public class LinkHeaderParseTests
{
    [Theory]
    [InlineData(
        "<https://api.github.com/user/starred?per_page=100&page=2>; rel=\"next\", <https://api.github.com/user/starred?per_page=100&page=20>; rel=\"last\"",
        "https://api.github.com/user/starred?per_page=100&page=2")]
    [InlineData(
        "<https://api.github.com/user/starred?per_page=100&page=19>; rel=\"prev\", <https://api.github.com/user/starred?per_page=100&page=21>; rel=\"next\", <https://api.github.com/user/starred?per_page=100&page=1>; rel=\"first\"",
        "https://api.github.com/user/starred?per_page=100&page=21")]
    public void Next_url_parses_without_angle_bracket(string header, string expected)
    {
        // Mirror of the client's parser (kept in sync below via the private helper
        // being internal — see HttpGitHubStarsClient.ParseNextLinkUrl).
        Assert.Equal(expected, HttpGitHubStarsClient.ParseNextLinkUrl(header));
    }
}
