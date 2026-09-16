namespace AgentFolio.Platform.Storage;

/// <summary>
/// Bucket-agnostic object store (spec 002 s3-03). Keys are server-derived
/// ("tenants/{tid}/sessions/{sid}/{sha256}{ext}") and never client input.
/// </summary>
public interface IObjectStore
{
    /// <summary>Stores <paramref name="data"/> at <paramref name="key"/>, overwriting if present.</summary>
    Task PutAsync(string key, Stream data, string contentType, CancellationToken ct = default);

    /// <summary>Opens the object for reading, or null when it does not exist.</summary>
    Task<Stream?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes every object whose key starts with <paramref name="prefix"/>. True when at least one was deleted.</summary>
    Task<bool> DeletePrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>Content length in bytes, or null when the object does not exist.</summary>
    Task<long?> StatAsync(string key, CancellationToken ct = default);

    /// <summary>Keys under <paramref name="prefix"/> (lexicographic). Used for session doc context.</summary>
    Task<IReadOnlyList<string>> ListByPrefixAsync(string prefix, CancellationToken ct = default);
}
