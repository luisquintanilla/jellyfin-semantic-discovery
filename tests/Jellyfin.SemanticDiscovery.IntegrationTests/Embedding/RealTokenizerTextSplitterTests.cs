using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Microsoft.Extensions.DataIngestion;

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
    public async Task DocumentAndTimedTextChunksRespectExactMiniLmInputLimit()
    {
        using MiniLmEmbeddingGenerator generator =
            MiniLmModelFixture.CreateGenerator();
        string longHeading =
            $"# {string.Join(' ', Enumerable.Repeat("heading", 280))}";
        string body = string.Join(
            ' ',
            Enumerable.Range(0, 900).Select(index => $"body{index}"));

        IReadOnlyList<ContentChunk> documentChunks =
            await ChunkMarkdownAsync(
                generator,
                $"{longHeading}\n\n{body}");
        IReadOnlyList<ContentChunk> timedTextChunks =
            await ChunkTimedTextAsync(generator, body);

        Assert.NotEmpty(documentChunks);
        Assert.NotEmpty(timedTextChunks);
        Assert.InRange(documentChunks.Count, 2, 32);
        Assert.All(
            documentChunks.Concat(timedTextChunks),
            chunk => Assert.InRange(
                generator.CountTokens(chunk.Text),
                1,
                MiniLmTokenBudget.MaximumInputTokens));
    }

    [ModelFact]
    public async Task MediHeaderChunkerUsesRawMarkdownHeadingContext()
    {
        using MiniLmEmbeddingGenerator generator =
            MiniLmModelFixture.CreateGenerator();
        ContentSourceDescriptor source = CreateSource(
            ContentSourceKind.Pdf,
            "book.pdf");
        IngestionDocument document =
            await new MarkdownIngestionDocumentAdapter().ReadAsync(
                source,
                "# Chapter\n\nBody paragraph.",
                CancellationToken.None);
        HeaderChunker chunker = new(new IngestionChunkerOptions(
            generator.Tokenizer)
        {
            MaxTokensPerChunk =
                MiniLmTokenBudget.MaximumInputTokens -
                MiniLmTokenBudget.EmbeddingSpecialTokens,
            OverlapTokens = 0,
        });

        IngestionChunk<string> chunk = Assert.Single(
            await CollectAsync(chunker.ProcessAsync(document)));

        Assert.Equal("# Chapter", chunk.Context);
        Assert.StartsWith(
            "# Chapter",
            chunk.Content,
            StringComparison.Ordinal);
        Assert.InRange(
            generator.CountTokens(chunk.Content),
            1,
            MiniLmTokenBudget.MaximumInputTokens);
    }

    [ModelFact]
    public async Task MediHeaderChunkerThrowsWhenHeadingConsumesBudget()
    {
        using MiniLmEmbeddingGenerator generator =
            MiniLmModelFixture.CreateGenerator();
        ContentSourceDescriptor source = CreateSource(
            ContentSourceKind.Epub,
            "book.epub");
        string heading =
            $"# {string.Join(' ', Enumerable.Repeat("heading", 260))}";
        IngestionDocument document =
            await new MarkdownIngestionDocumentAdapter().ReadAsync(
                source,
                $"{heading}\n\nbody",
                CancellationToken.None);
        HeaderChunker chunker = new(new IngestionChunkerOptions(
            generator.Tokenizer)
        {
            MaxTokensPerChunk =
                MiniLmTokenBudget.MaximumInputTokens -
                MiniLmTokenBudget.EmbeddingSpecialTokens,
            OverlapTokens = 0,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(chunker.ProcessAsync(document)));
    }

    private static void AssertBounded(
        string input,
        MiniLmEmbeddingGenerator generator)
    {
        IReadOnlyList<string> parts =
            new MiniLmTokenBudget(
                generator,
                MiniLmTokenBudget.MaximumInputTokens)
                .SplitNormalized(input);

        Assert.True(parts.Count > 1);
        Assert.Equal(input, string.Concat(parts));
        Assert.All(
            parts,
            part =>
            {
                Assert.True(
                    generator.CountTokens(part) <=
                    MiniLmTokenBudget.MaximumInputTokens);
                Assert.True(
                    part.EnumerateRunes().Count() <=
                    MiniLmTokenBudget.MaximumScalarsPerUnbrokenSegment);
            });
    }

    private static async Task<IReadOnlyList<ContentChunk>> ChunkMarkdownAsync(
        MiniLmEmbeddingGenerator generator,
        string markdown)
    {
        ContentSourceDescriptor source = CreateSource(
            ContentSourceKind.Pdf,
            "book.pdf");
        IngestionDocument document =
            await new MarkdownIngestionDocumentAdapter().ReadAsync(
                source,
                markdown,
                CancellationToken.None);
        return await new ContentChunkMapper(generator).MapAsync(
            source,
            new HeadingAwareChunker(generator).ProcessAsync(document),
            CancellationToken.None);
    }

    private static async Task<IReadOnlyList<ContentChunk>> ChunkTimedTextAsync(
        MiniLmEmbeddingGenerator generator,
        string text)
    {
        ContentSourceDescriptor source = CreateSource(
            ContentSourceKind.Subtitle,
            "movie.srt") with
        {
            Language = "en",
        };
        IngestionDocument document =
            TimedTextIngestionDocumentAdapter.Create(
                source,
                [new SubtitleCue(10, 20, text)]);
        return await new ContentChunkMapper(generator).MapAsync(
            source,
            new SubtitleChunker(generator).ProcessAsync(document),
            CancellationToken.None);
    }

    private static ContentSourceDescriptor CreateSource(
        ContentSourceKind kind,
        string path) => new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = kind,
            Path = path,
        };

    private static async Task<IReadOnlyList<T>> CollectAsync<T>(
        IAsyncEnumerable<T> values)
    {
        List<T> result = [];
        await foreach (T value in values)
        {
            result.Add(value);
        }

        return result;
    }
}
