using Slices.Stars;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#70 A2: README backfill — cursor, pacing, 404-as-terminal.</summary>
public class StarsReadmeBackfillTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"readme-bf-{Guid.NewGuid():N}.db");
    private readonly AgentFolio.Platform.SqliteStore _store;

    public StarsReadmeBackfillTests()
    {
        _store = new AgentFolio.Platform.SqliteStore(_dbPath);
        _store.OpenAsync().GetAwaiter().GetResult();
        _store.EnsureSchemaAsync().GetAwaiter().GetResult();
        _store.UpsertStarRawAsync(1, """{"id":1,"name":"has","owner":{"login":"o"}}""").GetAwaiter().GetResult();
        _store.UpsertStarRawAsync(2, """{"id":2,"name":"none","owner":{"login":"o"}}""").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p)) File.Delete(p);
    }

    private sealed class FakeClient : HttpGitHubStarsClient
    {
        public FakeClient() : base("t") { }
        public System.Collections.Generic.Dictionary<string, string?> Replies = new();

        internal override System.Threading.Tasks.Task<string?> GetReadmeAsync(string owner, string repo, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(Replies.TryGetValue(repo, out var v) ? v : throw new System.Net.Http.HttpRequestException("unexpected"));
    }

    [Fact]
    public async Task Batch_fetches_stores_readme_and_marks_404_terminal()
    {
        var http = new FakeClient { Replies = { ["has"] = "# Hi", ["none"] = null } };
        var notes = new System.Collections.Generic.List<string>();
        var svc = new StarsReadmeBackfillService(http, _store, onProgress: (n, s) => notes.Add(s), paceMilliseconds: 0);
        var (fetched, missing, skipped) = await svc.RunBatchAsync(System.Threading.CancellationToken.None);

        Assert.Equal(1, fetched);
        Assert.Equal(1, missing);
        Assert.Equal(0, skipped);
        Assert.Equal("# Hi", (await _store.GetStarRawAsync(1))!.ReadmeText);
        Assert.Equal("", (await _store.GetStarRawAsync(2))!.ReadmeText); // 404 = terminal empty
        Assert.Empty(await _store.GetStarIdsMissingReadmeAsync(10));
        Assert.True(notes.Count >= 2);
    }

    [Fact]
    public async Task Transient_errors_leave_row_for_retry()
    {
        var http = new FakeClient(); // Replies empty -> HttpRequestException for any repo
        var svc = new StarsReadmeBackfillService(http, _store, paceMilliseconds: 0);
        var (_, _, skipped) = await svc.RunBatchAsync(System.Threading.CancellationToken.None);
        Assert.Equal(2, skipped);
        Assert.Equal(2, (await _store.GetStarIdsMissingReadmeAsync(10)).Count); // still pending
    }
}
