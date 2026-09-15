namespace AgentFolio.Slices.Saves;

/// <summary>A single save ingest item received from a client.</summary>
public sealed record SaveIngestItem
{
    /// <summary>URL of the post being saved.</summary>
    public string PostUrl { get; init; } = string.Empty;

    /// <summary>Optional title of the post.</summary>
    public string? Title { get; init; }

    /// <summary>Optional author name.</summary>
    public string? AuthorName { get; init; }

    /// <summary>Optional ISO 8601 timestamp when saved.</summary>
    public string? SavedAtIso { get; init; }

    /// <summary>Optional excerpt/summary text.</summary>
    public string? Excerpt { get; init; }

    /// <summary>Optional full text snippet.</summary>
    public string? TextSnippet { get; init; }
}

/// <summary>Batch of save ingest items.</summary>
public sealed record SaveIngestBatch
{
    /// <summary>Items in the batch.</summary>
    public IReadOnlyList<SaveIngestItem> Items { get; init; } = new List<SaveIngestItem>();
}