namespace AgentFolio.UnitTests;

using System.Net;
using System.Net.Http.Json;
using AgentFolio.Platform.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

public class AuthTests
{
    // ---------- Argon2 hash/verify roundtrip ----------

    [Fact]
    public void HashThenVerify_CorrectPassword_ReturnsTrue()
    {
        var hash = PasswordGate.HashPassword("correct horse battery staple");
        Assert.True(PasswordGate.VerifyPassword("correct horse battery staple", hash));
    }

    [Fact]
    public void HashThenVerify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordGate.HashPassword("correct horse battery staple");
        Assert.False(PasswordGate.VerifyPassword("wrong password", hash));
    }

    [Fact]
    public void VerifyPassword_EmptyOrMalformedInputs_ReturnsFalse()
    {
        Assert.False(PasswordGate.VerifyPassword("", PasswordGate.HashPassword("x")));
        Assert.False(PasswordGate.VerifyPassword("x", ""));
        Assert.False(PasswordGate.VerifyPassword("x", "$argon2id$v=19$m=not,a,valid,hash"));
    }

    // ---------- Rate limiter ----------

    private sealed record FakeClock(DateTimeOffset Now)
    {
        public DateTimeOffset Advance(TimeSpan t) => Now + t;
    }

    [Fact]
    public void RateLimiter_FifthAttemptOk_SixthBlocked()
    {
        var limiter = new LoginRateLimiter();
        var t0 = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

        for (var i = 1; i <= 5; i++)
        {
            var r = limiter.CheckAndRecord("1.2.3.4", t0);
            Assert.True(r.Allowed, $"attempt {i} should be allowed");
        }

        var sixth = limiter.CheckAndRecord("1.2.3.4", t0);
        Assert.False(sixth.Allowed);
        Assert.True(sixth.RetryAfterSeconds > 0 && sixth.RetryAfterSeconds <= 60);
    }

    [Fact]
    public void RateLimiter_WindowResets_AfterSixtySeconds()
    {
        var limiter = new LoginRateLimiter();
        var t0 = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.CheckAndRecord("1.2.3.4", t0).Allowed);
        }
        Assert.False(limiter.CheckAndRecord("1.2.3.4", t0).Allowed);

        // Next fixed window (a new minute) allows attempts again.
        Assert.True(limiter.CheckAndRecord("1.2.3.4", t0.AddSeconds(60)).Allowed);
    }

    [Fact]
    public void RateLimiter_FiveConsecutiveFailures_LockoutThenExpiry()
    {
        var limiter = new LoginRateLimiter();
        var t0 = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

        for (var i = 0; i < 5; i++)
        {
            limiter.RecordFailure("5.6.7.8", t0);
        }

        // Immediately locked, even in a fresh fixed window.
        var locked = limiter.CheckAndRecord("5.6.7.8", t0.AddSeconds(61));
        Assert.False(locked.Allowed);
        Assert.InRange(locked.RetryAfterSeconds, 839, 900);

        // After 15 minutes the lockout expires.
        var after = limiter.CheckAndRecord("5.6.7.8", t0.AddSeconds(60).AddSeconds(900));
        Assert.True(after.Allowed);

        // A successful login resets consecutive failures.
        limiter.RecordFailure("9.9.9.9", t0);
        limiter.Reset("9.9.9.9");
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.CheckAndRecord("9.9.9.9", t0).Allowed);
        }
    }

    [Fact]
    public void RateLimiter_IndependentPerIp()
    {
        var limiter = new LoginRateLimiter();
        var t0 = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

        for (var i = 0; i < 5; i++)
        {
            limiter.CheckAndRecord("1.1.1.1", t0);
        }
        Assert.False(limiter.CheckAndRecord("1.1.1.1", t0).Allowed);
        Assert.True(limiter.CheckAndRecord("2.2.2.2", t0).Allowed);
    }

    // ---------- End-to-end via WebApplicationFactory ----------

    [Fact]
    public async Task AllResponses_CarryNoIndexHeader()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var ok = await client.GetAsync("/weatherforecast");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("noindex, nofollow", ok.Headers.TryGetValues("X-Robots-Tag", out var v1) ? string.Join(", ", v1) : null);

        var missing = await client.GetAsync("/does-not-exist");
        Assert.Equal("noindex, nofollow", missing.Headers.TryGetValues("X-Robots-Tag", out var v2) ? string.Join(", ", v2) : null);
    }

    [Fact]
    public async Task Login_WithCorrectPassword_Returns204AndCookie()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar, PasswordGate.HashPassword("hunter2"));

        var response = await client.PostAsJsonAsync("/auth/login", new { password = "hunter2" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(c => c.StartsWith("agentfolio_auth=")));
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401WithAttemptCount()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        Environment.SetEnvironmentVariable(PasswordGate.HashEnvVar, PasswordGate.HashPassword("hunter2"));

        var response = await client.PostAsJsonAsync("/auth/login", new { password = "nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("attemptCount", body);
    }
}
