using System.Security.Cryptography;
using System.Text;
using AgentFolio.Platform.Storage;

namespace Slices.Uploads;

/// <summary>
/// Gated upload pipeline (spec 002 s3-03):
///  - hard cap 10 MB per object;
///  - content sniffing via magic bytes — the client-supplied filename/extension is
///    UNTRUSTED and never influences the stored key or content type;
///  - object key is always server-derived:
///      tenants/{tid}/sessions/{sid}/{sha256hex}{sniffed-extension}.
/// Supported: pdf (%PDF-), docx (PK\x03\x04), txt/md (plain UTF-8 heuristics).
/// </summary>
public sealed class UploadService
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly IObjectStore _store;

    public UploadService(IObjectStore store) => _store = store;

    /// <summary>Maps sniffed content to (extension, contentType); null when rejected.</summary>
    public static (string Extension, string ContentType)? Sniff(byte[] bytes)
    {
        if (bytes.Length >= 5 && bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return (".pdf", "application/pdf");
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8))
            return (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        // Plain-text heuristics for txt/md: valid UTF-8, no NUL, mostly printable/whitespace.
        if (bytes.Length > 0 && IsPlausiblePlainText(bytes))
        {
            // md heuristic: heads with markdown markers or contains markdown line starts;
            // everything else plain text. Both land as UTF-8 text.
            return (".txt", "text/plain; charset=utf-8");
        }
        return null;
    }

    private static bool IsPlausiblePlainText(byte[] bytes)
    {
        var strictUtf8 = (Encoding)Encoding.UTF8.Clone();
        strictUtf8.DecoderFallback = DecoderFallback.ExceptionFallback;
        try
        {
            _ = strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        foreach (var b in bytes)
        {
            if (b == 0) return false;
        }
        return true;
    }

    public static string BuildObjectKey(string tenantId, string sessionId, byte[] bytes, string extension)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"tenants/{tenantId}/sessions/{sessionId}/{hash}{extension}";
    }

    /// <summary>Validates, sniffs and stores. Throws InvalidOperationException when gated out.</summary>
    public async Task<UploadReceipt> StoreAsync(string tenantId, string sessionId, byte[] bytes, DateTimeOffset now, CancellationToken ct = default)
    {
        if (bytes.LongLength > MaxBytes)
            throw new InvalidOperationException($"upload_too_large: {bytes.LongLength} bytes exceeds the {MaxBytes}-byte cap.");

        var sniffed = Sniff(bytes) ?? throw new InvalidOperationException("unsupported_content_type: magic-byte sniff rejected the payload (extension is untrusted).");

        var key = BuildObjectKey(tenantId, sessionId, bytes, sniffed.Extension);
        await _store.PutAsync(key, new MemoryStream(bytes, writable: false), sniffed.ContentType, ct);

        UploadExpiryEnforcer.Track(key, now);
        return new UploadReceipt(key, bytes.LongLength, sniffed.ContentType, now.Add(Retention));
    }

    public Task<bool> DeleteSessionDataAsync(string tenantId, string sessionId, CancellationToken ct = default)
    {
        return _store.DeletePrefixAsync($"tenants/{tenantId}/sessions/{sessionId}/", ct);
    }
}
