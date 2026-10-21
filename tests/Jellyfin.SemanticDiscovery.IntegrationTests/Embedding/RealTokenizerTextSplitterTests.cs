using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

public sealed class RealTokenizerTextSplitterTests
{
    [ModelFact]
    public void NoSpaceUnknownAndCjkInputsRespectTokenAndScalarBounds()
    {
        using MiniLmEmbeddingGenerator generator =
            MiniLmModelFixture.CreateGenerator();
        string latin = new('x', 350);
        string cjk = string.Concat(Enumerable.Repeat("漢", 350));

        AssertBounded(latin, generator);
        AssertBounded(cjk, generator);
    }

    [ModelFact]
    public void DocumentAndTimedTextChunksRespectExactMiniLmInputLimit()
    {
        using MiniLmEmbeddingGenerator generator =
            MiniLmModelFixture.CreateGenerator();
        string longHeading = $"# {string.Join(' ', Enumerable.Repeat("heading", 280))}";
        string body = string.Join(' ', Enumerable.Range(0, 900).Select(index => $"body{index}"));
        HeadingAwareChunker documentChunker = new(generator, maxTokens: 256);
        SubtitleChunker timedTextChunker = new(generator, maxTokens: 256);

        IReadOnlyList<ContentChunk> documentChunks = documentChunker.Chunk(
            Guid.NewGuid(),
            "book.pdf",
            ContentSourceKind.Pdf,
            $"{longHeading}\n\n{body}");
        IReadOnlyList<ContentChunk> timedTextChunks = timedTextChunker.Chunk(
            Guid.NewGuid(),
            "movie.srt",
            "en",
            ContentSourceKind.Subtitle,
            [new SubtitleCue(10, 20, body)]);

        Assert.NotEmpty(documentChunks);
        Assert.NotEmpty(timedTextChunks);
        Assert.All(
            documentChunks.Concat(timedTextChunks),
            chunk => Assert.InRange(generator.CountTokens(chunk.Text), 1, 256));
    }

    private static void AssertBounded(
        string input,
        MiniLmEmbeddingGenerator generator)
    {
        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(
            input,
            generator,
            maxTokens: 256);

        Assert.True(parts.Count > 1);
        Assert.Equal(input, string.Concat(parts));
        Assert.All(
            parts,
            part =>
            {
                Assert.True(generator.CountTokens(part) <= 256);
                Assert.True(
                    part.EnumerateRunes().Count() <=
                    TokenBoundedTextSplitter.MaximumScalarsPerUnbrokenSegment);
            });
    }
}
