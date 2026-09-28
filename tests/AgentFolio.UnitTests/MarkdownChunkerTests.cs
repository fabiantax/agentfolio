using Slices.Stars.Chunking;
using Xunit;

namespace AgentFolio.UnitTests;

/// <summary>#70 stage B1: markdown chunker — deterministic ids, ceiling guard, heading paths.</summary>
public class MarkdownChunkerTests
{
    [Fact]
    public void Empty_readme_yields_no_chunks()
    {
        Assert.Empty(MarkdownChunker.Chunk(1, ""));
        Assert.Empty(MarkdownChunker.Chunk(1, "   \n  "));
    }

    [Fact]
    public void Small_readme_is_single_chunk_with_deterministic_id()
    {
        var chunks = MarkdownChunker.Chunk(7, "# Title\n\nSome text about a repo.");
        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.Index);
        Assert.Equal(7, chunk.RepoId);
        Assert.Contains("Some text", chunk.Text);
    }

    [Fact]
    public void Big_readme_splits_and_all_chunks_are_under_ceiling()
    {
        var md = "# Big\n\n" + string.Join("\n\n", Enumerable.Range(0, 200)
            .Select(i => $"Paragraph {i}. " + new string('x', 300)));
        var chunks = MarkdownChunker.Chunk(1, md);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= MarkdownChunker.MaxChunkChars,
            $"chunk {c.Index} is {c.Text.Length} chars > ceiling"));
    }

    [Fact]
    public void Monster_paragraph_gets_sentence_split_under_ceiling()
    {
        var monster = "# Monolith\n\n" + string.Join(" ", Enumerable.Range(0, 600)
            .Select(i => $"Sentence number {i} says something meaningful here."));
        var chunks = MarkdownChunker.Chunk(1, monster);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= MarkdownChunker.MaxChunkChars));
        var recombined = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("Sentence number 599", recombined); // nothing dropped
    }

    [Fact]
    public void Heading_paths_track_hierarchy()
    {
        var md = "# A\n\ntext a\n## B\n\ntext b\n### C\n\ntext c";
        var chunks = MarkdownChunker.Chunk(1, md);
        Assert.Contains(chunks, c => c.HeadingPath.Contains("B") && c.Text.Contains("text b"));
        Assert.Contains(chunks, c => c.HeadingPath.Contains("C") && c.Text.Contains("text c"));
    }

    [Fact]
    public void Chunk_ids_are_stable_across_rebuilds()
    {
        var md = "# T\n\npara one\n\npara two\n\npara three";
        var a = MarkdownChunker.Chunk(9, md);
        var b = MarkdownChunker.Chunk(9, md);
        Assert.Equal(a.Select(c => (c.RepoId, c.Index, c.Text.Length)),
                     b.Select(c => (c.RepoId, c.Index, c.Text.Length)));
    }
}
