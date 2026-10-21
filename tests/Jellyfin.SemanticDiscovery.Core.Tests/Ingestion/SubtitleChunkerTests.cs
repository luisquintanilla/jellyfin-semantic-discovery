using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class SubtitleChunkerTests
{
    [Fact]
    public async Task ChunkGroupsCuesUntilTokenBudgetExceeded()
    {
        Guid ownerId = Guid.NewGuid();
        SubtitleCue[] cues =
        [
            new SubtitleCue(0, 10, "one two"),
            new SubtitleCue(10, 20, "three four"),
            new SubtitleCue(20, 30, "five six seven"),
        ];

        IReadOnlyList<ContentChunk> chunks =
            await ChunkAsync(cues, ownerId, maxTokens: 5);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("one two three four", chunks[0].Text);
        Assert.Equal("five six seven", chunks[1].Text);
        Assert.Equal(0, chunks[0].Ordinal);
        Assert.Equal(1, chunks[1].Ordinal);
    }

    [Fact]
    public async Task ChunkSplitsSingleCueExceedingTokenBudget()
    {
        SubtitleCue[] cues =
        [
            new SubtitleCue(
                0,
                100,
                "one two three four five six seven eight nine ten"),
        ];

        IReadOnlyList<ContentChunk> chunks =
            await ChunkAsync(cues, maxTokens: 5);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("one two three four five", chunks[0].Text);
        Assert.Equal("six seven eight nine ten", chunks[1].Text);
    }

    [Fact]
    public async Task ChunkUsesMinimumStartAndMaximumEndForOutOfOrderCues()
    {
        SubtitleCue[] cues =
        [
            new SubtitleCue(25, 40, "first"),
            new SubtitleCue(10, 20, "second"),
        ];

        ContentChunk chunk = Assert.Single(
            await ChunkAsync(cues, maxTokens: 50));

        Assert.Equal("first second", chunk.Text);
        Assert.Equal(10, chunk.StartTicks);
        Assert.Equal(40, chunk.EndTicks);
    }

    [Fact]
    public async Task ChunkPreservesLanguageOnEveryChunk()
    {
        SubtitleCue[] cues =
        [
            new SubtitleCue(0, 10, "one two"),
            new SubtitleCue(10, 20, "three four"),
        ];

        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            cues,
            language: "en",
            sourceKind: ContentSourceKind.Transcript,
            maxTokens: 2);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal("en", chunk.Language));
        Assert.All(
            chunks,
            chunk => Assert.Equal(
                ContentSourceKind.Transcript,
                chunk.SourceKind));
    }

    [Fact]
    public async Task ChunkProducesDeterministicIdsForIdenticalInput()
    {
        Guid ownerId = Guid.NewGuid();
        SubtitleCue[] cues = [new SubtitleCue(0, 10, "hello world")];

        IReadOnlyList<ContentChunk> first =
            await ChunkAsync(cues, ownerId, maxTokens: 50);
        IReadOnlyList<ContentChunk> second =
            await ChunkAsync(cues, ownerId, maxTokens: 50);

        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[0].ContentHash, second[0].ContentHash);
    }

    [Fact]
    public async Task ChunkProducesDifferentIdsForDifferentContent()
    {
        Guid ownerId = Guid.NewGuid();

        IReadOnlyList<ContentChunk> first = await ChunkAsync(
            [new SubtitleCue(0, 10, "hello world")],
            ownerId,
            maxTokens: 50);
        IReadOnlyList<ContentChunk> second = await ChunkAsync(
            [new SubtitleCue(0, 10, "goodbye world")],
            ownerId,
            maxTokens: 50);

        Assert.NotEqual(first[0].Id, second[0].Id);
        Assert.NotEqual(first[0].ContentHash, second[0].ContentHash);
    }

    [Fact]
    public async Task ChunkProjectsLanguageTicksOrdinalsHashesAndIdentity()
    {
        Guid ownerId = Guid.NewGuid();
        const string sourcePath = "episode.en.srt";
        SubtitleCue[] cues =
        [
            new SubtitleCue(30, 40, "one two three four"),
            new SubtitleCue(10, 20, "five six"),
        ];

        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            cues,
            ownerId,
            sourcePath,
            "en",
            ContentSourceKind.Subtitle,
            maxTokens: 3);

        Assert.Equal(
            ["one two three", "four five six"],
            chunks.Select(static chunk => chunk.Text));
        Assert.Equal([0, 1], chunks.Select(static chunk => chunk.Ordinal));
        Assert.Equal(
            [30L, 10L],
            chunks.Select(static chunk => chunk.StartTicks!.Value));
        Assert.Equal(
            [40L, 40L],
            chunks.Select(static chunk => chunk.EndTicks!.Value));
        Assert.All(
            chunks,
            chunk =>
            {
                Assert.Equal("en", chunk.Language);
                Assert.Equal(ContentHash.Compute(chunk.Text), chunk.ContentHash);
                Assert.Equal(
                    ContentChunkIdentity.Compute(
                        ownerId,
                        sourcePath,
                        ContentSourceKind.Subtitle,
                        chunk.Ordinal,
                        chunk.ContentHash),
                    chunk.Id);
            });
    }

    [Fact]
    public async Task ChunkReturnsEmptyForNoCues()
    {
        IReadOnlyList<ContentChunk> chunks =
            await ChunkAsync([]);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task CueMetadataFlowsThroughMediDocumentAndChunk()
    {
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Subtitle,
            Path = "episode.en.srt",
            Language = "en",
        };
        IngestionDocument document = TimedTextIngestionDocumentAdapter.Create(
            source,
            [new SubtitleCue(10, 20, "hello")]);
        IngestionDocumentParagraph paragraph = Assert.IsType<
            IngestionDocumentParagraph>(
            Assert.Single(document.EnumerateContent()));

        Assert.Equal(0, paragraph.Metadata[IngestionMetadata.CueOrdinal]);
        Assert.Equal(10L, paragraph.Metadata[IngestionMetadata.StartTicks]);
        Assert.Equal(20L, paragraph.Metadata[IngestionMetadata.EndTicks]);
        Assert.Equal("en", paragraph.Metadata[IngestionMetadata.Language]);

        IngestionChunk<string> chunk = Assert.Single(
            await CollectAsync(
                new SubtitleChunker(new WordCountTokenCounter())
                    .ProcessAsync(document)));
        Assert.Equal(
            [0],
            Assert.IsType<int[]>(
                chunk.Metadata[IngestionMetadata.CueOrdinals]));
        Assert.Equal(
            1,
            Assert.IsType<int>(
                chunk.Metadata[IngestionMetadata.TokenCount]));
    }

    private static async Task<IReadOnlyList<ContentChunk>> ChunkAsync(
        IReadOnlyList<SubtitleCue> cues,
        Guid? ownerId = null,
        string sourcePath = "subs.srt",
        string? language = null,
        ContentSourceKind sourceKind = ContentSourceKind.Subtitle,
        int maxTokens = 5)
    {
        WordCountTokenCounter counter = new();
        ContentSourceDescriptor source = new()
        {
            OwnerId = ownerId ?? Guid.NewGuid(),
            Kind = sourceKind,
            Path = sourcePath,
            Language = language,
        };
        IngestionDocument document =
            TimedTextIngestionDocumentAdapter.Create(source, cues);
        return await new ContentChunkMapper(counter, maxTokens).MapAsync(
            source,
            new SubtitleChunker(counter, maxTokens).ProcessAsync(document),
            CancellationToken.None);
    }

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
