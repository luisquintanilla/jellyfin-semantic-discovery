using System.Reflection;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class HeadingAwareChunkerTests
{
    private static HeadingAwareChunker CreateChunker(int maxTokens = 50) =>
        new(new WordCountTokenCounter(), maxTokens);

    [Fact]
    public void ChunkBuildsHeadingAncestryPathFromNestedHeadings()
    {
        HeadingAwareChunker chunker = CreateChunker();
        string markdown = "# Chapter 1\n\nIntro paragraph.\n\n## Section A\n\nSection A content.\n";

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            Guid.NewGuid(), "book.pdf", ContentSourceKind.Pdf, markdown);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Chapter 1", chunks[0].Heading);
        Assert.Equal("Chapter 1 > Section A", chunks[1].Heading);
    }

    [Fact]
    public void ChunkResetsDeeperHeadingLevelsWhenAShallowerHeadingAppears()
    {
        HeadingAwareChunker chunker = CreateChunker();
        string markdown =
            "# Chapter 1\n\n## Section A\n\nFirst.\n\n# Chapter 2\n\nSecond.\n";

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            Guid.NewGuid(), "book.pdf", ContentSourceKind.Pdf, markdown);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Chapter 1 > Section A", chunks[0].Heading);
        // "Chapter 2" (a new H1) clears the stale H2 ancestor from "Chapter 1".
        Assert.Equal("Chapter 2", chunks[1].Heading);
    }

    [Fact]
    public void ChunkProducesDeterministicIdsOrdinalsAndHashesForIdenticalInput()
    {
        HeadingAwareChunker chunker = CreateChunker();
        Guid ownerId = Guid.NewGuid();
        string markdown = "# Heading\n\nBody text.\n";

        IReadOnlyList<ContentChunk> first =
            chunker.Chunk(ownerId, "book.pdf", ContentSourceKind.Pdf, markdown);
        IReadOnlyList<ContentChunk> second =
            chunker.Chunk(ownerId, "book.pdf", ContentSourceKind.Pdf, markdown);

        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[0].ContentHash, second[0].ContentHash);
        Assert.Equal(0, first[0].Ordinal);
    }

    [Fact]
    public void ChunkProducesDifferentIdsForDifferentContent()
    {
        HeadingAwareChunker chunker = CreateChunker();
        Guid ownerId = Guid.NewGuid();

        IReadOnlyList<ContentChunk> first =
            chunker.Chunk(ownerId, "book.pdf", ContentSourceKind.Pdf, "# H\n\nOne.\n");
        IReadOnlyList<ContentChunk> second =
            chunker.Chunk(ownerId, "book.pdf", ContentSourceKind.Pdf, "# H\n\nTwo.\n");

        Assert.NotEqual(first[0].Id, second[0].Id);
        Assert.NotEqual(first[0].ContentHash, second[0].ContentHash);
    }

    [Fact]
    public void ChunkRespectsTokenBudgetIncludingHeadingPrefixReSplit()
    {
        HeadingAwareChunker chunker = new(new WordCountTokenCounter(), maxTokens: 3);
        string markdown = "## Head One\n\na b c\n";

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            Guid.NewGuid(), "book.epub", ContentSourceKind.Epub, markdown);

        // The heading fits its own budget unbounded, so the body must be
        // split across more than one chunk to keep heading+body within the
        // overall token budget on every chunk.
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.Equal("Head One", chunk.Heading));
        Assert.All(
            chunks,
            chunk => Assert.True(
                chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3));
        Assert.All(
            chunks,
            chunk => Assert.StartsWith("Head One", chunk.Text, StringComparison.Ordinal));

        // The original body words are preserved, in order, once the
        // "{heading}\n{body}" prefix is stripped back off of each chunk.
        string[] recombinedBodyWords = chunks
            .SelectMany(chunk => chunk.Text.Split('\n', 2)[1]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        Assert.Equal(["a", "b", "c"], recombinedBodyWords);
    }

    [Fact]
    public void ChunkSplitsOversizedSectionWithoutAHeadingIntoMultipleChunks()
    {
        HeadingAwareChunker chunker = new(new WordCountTokenCounter(), maxTokens: 3);
        string markdown = "word1 word2 word3 word4 word5 word6 word7\n";

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            Guid.NewGuid(), "book.pdf", ContentSourceKind.Pdf, markdown);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("word1 word2 word3", chunks[0].Text);
        Assert.Equal("word4 word5 word6", chunks[1].Text);
        Assert.Equal("word7", chunks[2].Text);
        Assert.All(chunks, chunk => Assert.Null(chunk.Heading));
    }

    [Fact]
    public void ChunkShrinksExtremeHeadingAgainstCombinedSpecialTokenCount()
    {
        SpecialTokenCounter counter = new();
        HeadingAwareChunker chunker = new(counter, maxTokens: 6);
        string markdown =
            "# one two three four five six seven eight\n\nbody\n";

        ContentChunk chunk = Assert.Single(chunker.Chunk(
            Guid.NewGuid(),
            "book.epub",
            ContentSourceKind.Epub,
            markdown));

        Assert.Equal("body", chunk.Text.Split('\n')[^1]);
        Assert.False(string.IsNullOrWhiteSpace(chunk.Heading));
        Assert.True(counter.CountTokens(chunk.Text) <= 6);
    }

    private sealed class SpecialTokenCounter : ITokenCounter
    {
        public int CountTokens(string text) =>
            TextNormalization.NormalizeWhitespace(text)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Length + 2;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n   \n")]
    [InlineData("# Heading Only\n\n")]
    public void ChunkReturnsEmptyForBlankOrHeadingOnlyMarkdown(string markdown)
    {
        HeadingAwareChunker chunker = CreateChunker();

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            Guid.NewGuid(), "book.pdf", ContentSourceKind.Pdf, markdown);

        Assert.Empty(chunks);
    }

    [Theory]
    [InlineData(ContentSourceKind.Subtitle)]
    [InlineData(ContentSourceKind.Transcript)]
    public void ChunkRejectsUnsupportedSourceKind(ContentSourceKind kind)
    {
        HeadingAwareChunker chunker = CreateChunker();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => chunker.Chunk(Guid.NewGuid(), "book.pdf", kind, "text"));
    }

    [Fact]
    public void ConstructorOnlyDependsOnTokenCounterNotAnEmbeddingGenerator()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(HeadingAwareChunker).GetConstructors());
        ParameterInfo[] parameters = constructor.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(ITokenCounter), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
    }
}
