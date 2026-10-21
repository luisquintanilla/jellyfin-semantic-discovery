using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class SubtitleChunkerTests
{
    private static SubtitleChunker CreateChunker(int maxTokens = 5) =>
        new(new WordCountTokenCounter(), maxTokens);

    [Fact]
    public void ChunkGroupsCuesUntilTokenBudgetExceeded()
    {
        SubtitleChunker chunker = CreateChunker(maxTokens: 5);
        Guid ownerId = Guid.NewGuid();
        SubtitleCue[] cues =
        [
            new SubtitleCue(0, 10, "one two"),
            new SubtitleCue(10, 20, "three four"),
            new SubtitleCue(20, 30, "five six seven"),
        ];

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            ownerId,
            "subs.srt",
            language: null,
            ContentSourceKind.Subtitle,
            cues);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("one two three four", chunks[0].Text);
        Assert.Equal("five six seven", chunks[1].Text);
        Assert.Equal(0, chunks[0].Ordinal);
        Assert.Equal(1, chunks[1].Ordinal);
    }

    [Fact]
    public void ChunkSplitsSingleCueExceedingTokenBudget()
    {
        SubtitleChunker chunker = CreateChunker(maxTokens: 5);
        Guid ownerId = Guid.NewGuid();
        SubtitleCue[] cues =
        [
            new SubtitleCue(0, 100, "one two three four five six seven eight nine ten"),
        ];

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            ownerId,
            "subs.srt",
            language: null,
            ContentSourceKind.Subtitle,
            cues);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("one two three four five", chunks[0].Text);
        Assert.Equal("six seven eight nine ten", chunks[1].Text);
    }

    [Fact]
    public void ChunkUsesMinimumStartAndMaximumEndForOutOfOrderCues()
    {
        SubtitleChunker chunker = CreateChunker(maxTokens: 50);
        Guid ownerId = Guid.NewGuid();
        SubtitleCue[] cues =
        [
            new SubtitleCue(25, 40, "first"),
            new SubtitleCue(10, 20, "second"),
        ];

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            ownerId,
            "subs.srt",
            language: null,
            ContentSourceKind.Subtitle,
            cues);

        ContentChunk chunk = Assert.Single(chunks);
        Assert.Equal("first second", chunk.Text);
        Assert.Equal(10, chunk.StartTicks);
        Assert.Equal(40, chunk.EndTicks);
    }

    [Fact]
    public void ChunkPreservesLanguageOnEveryChunk()
    {
        SubtitleChunker chunker = CreateChunker(maxTokens: 2);
        Guid ownerId = Guid.NewGuid();
        SubtitleCue[] cues =
        [
            new SubtitleCue(0, 10, "one two"),
            new SubtitleCue(10, 20, "three four"),
        ];

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            ownerId,
            "subs.srt",
            language: "en",
            ContentSourceKind.Transcript,
            cues);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal("en", chunk.Language));
        Assert.All(chunks, chunk => Assert.Equal(ContentSourceKind.Transcript, chunk.SourceKind));
    }

    [Fact]
    public void ChunkProducesDeterministicIdsForIdenticalInput()
    {
        SubtitleChunker chunker = CreateChunker(maxTokens: 50);
        Guid ownerId = Guid.NewGuid();
        SubtitleCue[] cues = [new SubtitleCue(0, 10, "hello world")];

        IReadOnlyList<ContentChunk> first = chunker.Chunk(
            ownerId, "subs.srt", null, ContentSourceKind.Subtitle, cues);
        IReadOnlyList<ContentChunk> second = chunker.Chunk(
            ownerId, "subs.srt", null, ContentSourceKind.Subtitle, cues);

        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[0].ContentHash, second[0].ContentHash);
    }

    [Fact]
    public void ChunkProducesDifferentIdsForDifferentContent()
    {
        SubtitleChunker chunker = CreateChunker(maxTokens: 50);
        Guid ownerId = Guid.NewGuid();

        IReadOnlyList<ContentChunk> first = chunker.Chunk(
            ownerId, "subs.srt", null, ContentSourceKind.Subtitle,
            [new SubtitleCue(0, 10, "hello world")]);
        IReadOnlyList<ContentChunk> second = chunker.Chunk(
            ownerId, "subs.srt", null, ContentSourceKind.Subtitle,
            [new SubtitleCue(0, 10, "goodbye world")]);

        Assert.NotEqual(first[0].Id, second[0].Id);
        Assert.NotEqual(first[0].ContentHash, second[0].ContentHash);
    }

    [Fact]
    public void ChunkProjectsLanguageTicksOrdinalsHashesAndIdentity()
    {
        SubtitleChunker chunker = CreateChunker(maxTokens: 3);
        Guid ownerId = Guid.NewGuid();
        const string sourcePath = "episode.en.srt";
        SubtitleCue[] cues =
        [
            new SubtitleCue(30, 40, "one two three four"),
            new SubtitleCue(10, 20, "five six"),
        ];

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            ownerId,
            sourcePath,
            "en",
            ContentSourceKind.Subtitle,
            cues);

        Assert.Equal(["one two three", "four five six"], chunks.Select(static chunk => chunk.Text));
        Assert.Equal([0, 1], chunks.Select(static chunk => chunk.Ordinal));
        Assert.Equal([30, 10], chunks.Select(static chunk => chunk.StartTicks));
        Assert.Equal([40, 40], chunks.Select(static chunk => chunk.EndTicks));
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
    public void ChunkReturnsEmptyForNoCues()
    {
        SubtitleChunker chunker = CreateChunker();

        IReadOnlyList<ContentChunk> chunks = chunker.Chunk(
            Guid.NewGuid(), "subs.srt", null, ContentSourceKind.Subtitle, []);

        Assert.Empty(chunks);
    }
}
