namespace AgentFolio.Platform.Auth;

using System.Collections.Concurrent;

/// <summary>Login rate-limit decision.</summary>
public record LoginRateLimitResult(bool Allowed, int AttemptCount, int RetryAfterSeconds);

/// <summary>Abstraction so tests can fake time.</summary>
public interface ILoginRateLimiter
{
    /// <summary>Records an attempt for <paramref name="ip"/> and returns whether it may proceed.</summary>
    LoginRateLimitResult CheckAndRecord(string ip, DateTimeOffset now);

    /// <summary>Records a failed authentication for <paramref name="ip"/>.</summary>
    void RecordFailure(string ip, DateTimeOffset now);

    /// <summary>Resets state for <paramref name="ip"/> after successful login.</summary>
    void Reset(string ip);
}

/// <summary>
/// Fixed-window limiter: max 5 attempts/min per IP; after 5 consecutive
/// failures the IP is locked out for 15 minutes. In-memory, single instance.
/// </summary>
public sealed class LoginRateLimiter : ILoginRateLimiter
{
    private const int MaxAttemptsPerWindow = 5;
    private const int WindowSeconds = 60;
    private const int MaxConsecutiveFailures = 5;
    private const int LockoutSeconds = 900;

    private sealed record WindowState(int WindowIndex, int Attempts);

    private readonly ConcurrentDictionary<string, WindowState> _windows = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)> _failures = new();

    public LoginRateLimitResult CheckAndRecord(string ip, DateTimeOffset now)
    {
        if (_failures.TryGetValue(ip, out var f) && f.LockedUntil > now)
        {
            return new LoginRateLimitResult(false, f.Failures, (int)Math.Ceiling((f.LockedUntil - now).TotalSeconds));
        }

        var windowIndex = (int)(now.ToUnixTimeSeconds() / WindowSeconds);
        var state = _windows.AddOrUpdate(
            ip,
            _ => new WindowState(windowIndex, 1),
            (_, existing) => existing.WindowIndex == windowIndex
                ? existing with { Attempts = existing.Attempts + 1 }
                : new WindowState(windowIndex, 1));

        var attemptsThisWindow = state.WindowIndex == windowIndex ? state.Attempts : 1;
        var retryAfter = WindowSeconds - (int)(now.ToUnixTimeSeconds() % WindowSeconds);
        return new LoginRateLimitResult(attemptsThisWindow <= MaxAttemptsPerWindow, attemptsThisWindow, retryAfter);
    }

    public void RecordFailure(string ip, DateTimeOffset now)
    {
        _failures.AddOrUpdate(
            ip,
            _ => (1, DateTimeOffset.MinValue),
            (_, existing) =>
            {
                var failures = existing.Failures + 1;
                var lockedUntil = failures >= MaxConsecutiveFailures
                    ? now.AddSeconds(LockoutSeconds)
                    : DateTimeOffset.MinValue;
                return (failures, lockedUntil);
            });
    }

    public void Reset(string ip) => _failures.TryRemove(ip, out _);
}
