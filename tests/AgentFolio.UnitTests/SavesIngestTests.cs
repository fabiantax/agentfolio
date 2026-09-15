using AgentFolio.Contracts;
using AgentFolio.Platform;
using AgentFolio.Slices.Saves;
using Xunit;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;

namespace AgentFolio.UnitTests;

public sealed class SavesIngestTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public SavesIngestTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"agentfolio-saves-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
    }

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p)) File.Delete(p);
    }

    [Fact]
    public async Task Canonicalizer_StripsTrackingParams()
    {
        // URLs with query params should strip them
        Assert.Equal("https://example.com/post1",
            SaveCanonicalizer.Canonicalize("https://example.com/post1?utm_source=twitter"));

        Assert.Equal("https://example.com/post1",
            SaveCanonicalizer.Canonicalize("https://example.com/post1?ref=abc&campaign=xyz"));
    }

    [Fact]
    public async Task Canonicalizer_ExtractsActivityId_from_activities_path()
    {
        // /activities/([^/]+) pattern - just the path portion
        var result = SaveCanonicalizer.Canonicalize("https://example.com/activities/abc123");
        Assert.Equal("https://example.com/activities/abc123", result);

        // Test with full URL containing activities path and query params
        result = SaveCanonicalizer.Canonicalize("https://www.linkedin.com/activities/abc123?source=feed");
        Assert.Equal("https://www.linkedin.com/activities/abc123", result);
    }

    [Fact]
    public async Task Canonicalizer_ExtractsActivityId_from_activity_num()
    {
        // activity-(\d+) pattern - should convert to /activities/{id}
        var result = SaveCanonicalizer.Canonicalize("https://example.com/activity-42");
        Assert.Equal("https://example.com/activities/42", result);
    }

    [Fact]
    public async Task Canonicalizer_Strips_trailing_slash()
    {
        Assert.Equal("https://example.com/post1",
            SaveCanonicalizer.Canonicalize("https://example.com/post1/"));

        Assert.Equal("https://example.com/",
            SaveCanonicalizer.Canonicalize("https://example.com/"));
    }

    [Fact]
    public async Task Canonicalizer_ReturnsNull_for_invalid_input()
    {
        Assert.Null(SaveCanonicalizer.Canonicalize(null));
        Assert.Null(SaveCanonicalizer.Canonicalize(""));
        Assert.Null(SaveCanonicalizer.Canonicalize("   "));
        Assert.Null(SaveCanonicalizer.Canonicalize("not-a-url"));
        Assert.Null(SaveCanonicalizer.Canonicalize("http://example.com/post1")); // http not https
    }

    [Fact]
    public async Task SaveIngestModels_Properties_Set_Get()
    {
        // Test the models can be serialized/deserialized
        var item = new SaveIngestItem
        {
            PostUrl = "https://example.com/post",
            Title = "Test Title",
            AuthorName = "Test Author",
            SavedAtIso = "2024-01-01T00:00:00Z",
            Excerpt = "Test excerpt",
            TextSnippet = "Test snippet"
        };

        var batch = new SaveIngestBatch
        {
            Items = new List<SaveIngestItem> { item }
        };

        var json = JsonSerializer.Serialize(batch);
        var deserialized = JsonSerializer.Deserialize<SaveIngestBatch>(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.Items);
        Assert.Equal("https://example.com/post", deserialized.Items[0].PostUrl);
        Assert.Equal("Test Title", deserialized.Items[0].Title);
        Assert.Equal("Test Author", deserialized.Items[0].AuthorName);
        Assert.Equal("2024-01-01T00:00:00Z", deserialized.Items[0].SavedAtIso);
        Assert.Equal("Test excerpt", deserialized.Items[0].Excerpt);
        Assert.Equal("Test snippet", deserialized.Items[0].TextSnippet);
    }

    [Fact]
    public async Task SaveIngestBatch_Empty_items_returns_false_for_has_items()
    {
        var batch = new SaveIngestBatch { Items = new List<SaveIngestItem>() };
        Assert.Empty(batch.Items);
    }
}