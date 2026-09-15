namespace AgentFolio.Platform.Auth;

using Isopoh.Cryptography.Argon2;

/// <summary>
/// Argon2id password verification against the encoded hash from
/// <c>AGENTFOLIO_PASSWORD_HASH</c>.
/// </summary>
public static class PasswordGate
{
    /// <summary>Environment variable holding the encoded Argon2 hash.</summary>
    public const string HashEnvVar = "AGENTFOLIO_PASSWORD_HASH";

    /// <summary>
    /// Verifies <paramref name="password"/> against an encoded Argon2 hash string.
    /// </summary>
    public static bool VerifyPassword(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encodedHash))
        {
            return false;
        }

        try
        {
            return Argon2.Verify(encodedHash, password);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hashes <paramref name="password"/> into an encoded Argon2id string
    /// (test fixtures + first-boot setup only).
    /// </summary>
    public static string HashPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Argon2.Hash(
            password,
            timeCost: 3,
            memoryCost: 65536,
            parallelism: 1,
            type: Argon2Type.HybridAddressing,
            hashLength: 32,
            secureArrayCall: Isopoh.Cryptography.SecureArray.SecureArray.DefaultCall);
    }
}
