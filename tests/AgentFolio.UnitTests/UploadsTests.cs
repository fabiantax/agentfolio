using System.Text;
using AgentFolio.Platform.Storage;
using Slices.Uploads;

namespace AgentFolio.UnitTests;

/// <summary>s3-03: object store + gated uploads + lifecycle enforcer.</summary>
public class UploadsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static byte[] PdfBytes() => Encoding.ASCII.GetBytes("%PDF-1.7 fake pdf body");
    private static byte[] DocxBytes() => [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x63, 0x64];
    private static byte[] TxtBytes() => Encoding.UTF8.GetBytes("plain text upload\nsecond line\n");
    private static byte[] ExeWithPdfName() => [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];

    // ---- Object store happy path ----

    [Fact]
    public async Task ObjectStore_PutStatGetDeletePrefix_RoundTrips()
    {
        var store = new InMemoryObjectStore();
        var bytes = PdfBytes();

        await store.PutAsync("tenants/t1/sessions/s1/abc.pdf", new MemoryStream(bytes, writable: false), "application/pdf");

        Assert.Equal((long?)bytes.LongLength, await store.StatAsync("tenants/t1/sessions/s1/abc.pdf"));
        Assert.Null(await store.StatAsync("tenants/t1/sessions/s1/missing.pdf"));

        await using var got = await store.GetAsync("tenants/t1/sessions/s1/abc.pdf");
        Assert.NotNull(got);
        using var ms = new MemoryStream();
        await got.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());

        Assert.True(await store.DeletePrefixAsync("tenants/t1/sessions/s1/"));
        Assert.Null(await store.StatAsync("tenants/t1/sessions/s1/abc.pdf"));
        Assert.False(await store.DeletePrefixAsync("tenants/t1/sessions/s1/"));
    }

    // ---- Content sniffing gates ----

    [Fact]
    public async Task SpoofedExeWithPdfExtension_IsRejected()
    {
        var uploads = new UploadService(new InMemoryObjectStore());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => uploads.StoreAsync("t1", "s1", ExeWithPdfName(), Now));
        Assert.Contains("unsupported_content_type", ex.Message);
    }

    [Fact]
    public async Task OverTenMegabytes_IsRejected()
    {
        var uploads = new UploadService(new InMemoryObjectStore());
        var big = new byte[UploadService.MaxBytes + 1];
        big[0] = (byte)'%';
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => uploads.StoreAsync("t1", "s1", big, Now));
        Assert.Contains("upload_too_large", ex.Message);
    }

    [Fact]
    public async Task Sniffer_AcceptsPdfDocxAndPlainText()
    {
        Assert.Equal((".pdf", "application/pdf"), UploadService.Sniff(PdfBytes()));
        Assert.Equal((".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            UploadService.Sniff(DocxBytes()));
        Assert.Equal((".txt", "text/plain; charset=utf-8"), UploadService.Sniff(TxtBytes()));
        Assert.Null(UploadService.Sniff([]));
    }

    // ---- Server-derived keys ----

    [Fact]
    public void ObjectKey_IsServerDerived_RegardlessOfClientFilename()
    {
        // Same bytes must map to the same key no matter what the client called them.
        var a = UploadService.BuildObjectKey("t1", "s1", TxtBytes(), ".txt");
        var b = UploadService.BuildObjectKey("t1", "s1", TxtBytes(), ".txt");
        Assert.Equal(a, b);

        // Format: tenants/{tid}/sessions/{sid}/{64-hex-sha256}{ext}
        var parts = a.Split('/');
        Assert.Equal(5, parts.Length);
        Assert.Equal("tenants", parts[0]);
        Assert.Equal("t1", parts[1]);
        Assert.Equal("sessions", parts[2]);
        Assert.Equal("s1", parts[3]);
        var stem = parts[4];
        Assert.EndsWith(".txt", stem, StringComparison.Ordinal);
        var hex = stem[..^4];
        Assert.Equal(64, hex.Length);
        Assert.All(hex, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"non-hex char {c}"));

        // Deterministic: key equals sha256 hex of the exact bytes.
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(TxtBytes()));
        Assert.Equal($"tenants/t1/sessions/s1/{sha}.txt", a);
    }

    [Fact]
    public async Task StoreAsync_IssuesReceiptWith24hExpiry_AndTracksPrefix()
    {
        UploadExpiryEnforcer.ClearRegistry();
        var uploads = new UploadService(new InMemoryObjectStore());
        var receipt = await uploads.StoreAsync("t1", "s1", PdfBytes(), Now);

        Assert.Equal(UploadService.BuildObjectKey("t1", "s1", PdfBytes(), ".pdf"), receipt.Key);
        Assert.Equal(PdfBytes().LongLength, receipt.Bytes);
        Assert.Equal("application/pdf", receipt.ContentType);
        Assert.Equal(Now.AddHours(24), receipt.ExpiresAt);
        Assert.Equal(1, UploadExpiryEnforcer.TrackedCount);
    }

    [Fact]
    public async Task DeleteSessionDataAsync_WipesSessionPrefix()
    {
        var store = new InMemoryObjectStore();
        var uploads = new UploadService(store);
        await uploads.StoreAsync("t1", "s1", PdfBytes(), Now);
        await uploads.StoreAsync("t1", "s1", TxtBytes(), Now);
        await uploads.StoreAsync("t1", "s2", PdfBytes(), Now); // other session, must survive

        Assert.True(await uploads.DeleteSessionDataAsync("t1", "s1"));
        Assert.Null(await store.StatAsync(UploadService.BuildObjectKey("t1", "s1", PdfBytes(), ".pdf")));
        Assert.NotNull(await store.StatAsync(UploadService.BuildObjectKey("t1", "s2", PdfBytes(), ".pdf")));
    }

    // ---- Expiry enforcer with fake clock ----

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Enforcer_WithFakeClock_ExpiresOldPrefix_KeepsFreshOne()
    {
        UploadExpiryEnforcer.ClearRegistry();
        var store = new InMemoryObjectStore();
        var uploads = new UploadService(store);

        var oldReceipt = await uploads.StoreAsync("t1", "old-s", PdfBytes(), Now.AddHours(-30));
        var freshReceipt = await uploads.StoreAsync("t1", "fresh-s", PdfBytes(), Now.AddHours(-1));

        var enforcer = new UploadExpiryEnforcer(store, new FakeClock(Now));
        var reaped = await enforcer.SweepAsync(Now);

        var oldPrefix = "tenants/t1/sessions/old-s/";
        var freshPrefix = "tenants/t1/sessions/fresh-s/";
        Assert.Equal([oldPrefix], reaped);
        Assert.Null(await store.StatAsync(oldReceipt.Key));
        Assert.NotNull(await store.StatAsync(freshReceipt.Key));
        Assert.Equal(1, UploadExpiryEnforcer.TrackedCount); // fresh one still tracked
        Assert.StartsWith(freshPrefix, freshReceipt.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enforcer_AtRetentionBoundary_IsKept()
    {
        UploadExpiryEnforcer.ClearRegistry();
        var store = new InMemoryObjectStore();
        var uploads = new UploadService(store);
        var receipt = await uploads.StoreAsync("t1", "edge-s", PdfBytes(), Now);

        var enforcer = new UploadExpiryEnforcer(store, new FakeClock(Now.Add(UploadService.Retention)));
        var reaped = await enforcer.SweepAsync(Now.Add(UploadService.Retention));

        // Exactly-at-retention is not "older than"; survives.
        Assert.Empty(reaped);
        Assert.NotNull(await store.StatAsync(receipt.Key));
    }
}
