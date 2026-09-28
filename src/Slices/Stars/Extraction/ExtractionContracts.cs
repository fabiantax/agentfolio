namespace Slices.Stars.Extraction;

/// <summary>One extracted entity mention (span-aligned to the chunk text).</summary>
public sealed record ExtractedEntity(
    string Text,
    string Label,
    int Start,
    int End,
    double Score);

/// <summary>One extracted relation (head --relation-> tail, both as spans).</summary>
public sealed record ExtractedRelation(
    string HeadText,
    string HeadLabel,
    string TailText,
    string TailLabel,
    string Relation,
    double Score);

/// <summary>Result of extracting over one text (chunk).</summary>
public sealed record ExtractionResult(
    IReadOnlyList<ExtractedEntity> Entities,
    IReadOnlyList<ExtractedRelation> Relations);

/// <summary>
/// The extraction seam (#66 B2/B3, DIP): chunk text in, entities+relations out.
/// Adapters: GLiNER 2.5 batch (:8095), GLiNER-Relex (:8090), LLM (MAF IChatClient).
/// </summary>
public interface IEntityExtractor
{
    /// <summary>Extract over a batch of texts. Implementations MUST batch (single calls are ~2x slower per text).</summary>
    Task<IReadOnlyList<ExtractionResult>> ExtractAsync(
        IReadOnlyList<string> texts,
        IReadOnlyList<string> entityLabels,
        CancellationToken ct = default);
}

public interface IRelationExtractor
{
    Task<IReadOnlyList<ExtractionResult>> ExtractRelationsAsync(
        IReadOnlyList<string> texts,
        IReadOnlyList<string> entityLabels,
        IReadOnlyList<string> relationLabels,
        CancellationToken ct = default);
}
