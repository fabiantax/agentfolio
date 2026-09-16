namespace Slices.Uploads;

/// <summary>Server-issued receipt for an accepted upload. Key is server-derived.</summary>
public sealed record UploadReceipt(string Key, long Bytes, string ContentType, DateTimeOffset ExpiresAt);
