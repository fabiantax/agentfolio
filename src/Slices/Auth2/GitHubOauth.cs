using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Slices.Auth2;

public sealed record GitHubUser(long Id, string Login);

/// <summary>
/// GitHub OAuth authorization-code flow with PKCE (spec 002 s2-02).
/// Pure helpers: URL building and token/user exchange over a provided HttpClient.
/// </summary>
public static class GitHubOauth
{
    public const string AuthorizeUrl = "https://github.com/login/oauth/authorize";
    public const string TokenUrl = "https://github.com/login/oauth/access_token";
    public const string UserUrl = "https://api.github.com/user";

    /// <summary>Base64url without padding.</summary>
    public static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>PKCE verifier: 32 random bytes, base64url.</summary>
    public static string NewVerifier() => B64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>S256 code challenge: base64url(SHA256(verifier)).</summary>
    public static string CodeChallengeS256(string verifier) =>
        B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string BuildStartUrl(string clientId, string redirectUri, string state, string codeChallenge)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = "read:user",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        var query = string.Join("&", q.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AuthorizeUrl}?{query}";
    }

    private sealed record TokenResponse(string? access_token);

    /// <summary>Exchanges the authorization code for an access token.</summary>
    public static async Task<string> ExchangeCodeAsync(
        HttpClient http, string clientId, string clientSecret,
        string code, string redirectUri, string verifier)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Accept.Add(new("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return payload?.access_token
            ?? throw new InvalidOperationException("github_oauth_token_missing");
    }

    /// <summary>Fetches the authenticated GitHub user.</summary>
    public static async Task<GitHubUser> GetUserAsync(HttpClient http, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserUrl);
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.UserAgent.ParseAdd("AgentFolio");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<GitHubUser>();
        return user ?? throw new InvalidOperationException("github_oauth_user_missing");
    }

    /// <summary>OAuth state: 32 random bytes, base64url.</summary>
    public static string NewState() => B64Url(RandomNumberGenerator.GetBytes(32));
}
