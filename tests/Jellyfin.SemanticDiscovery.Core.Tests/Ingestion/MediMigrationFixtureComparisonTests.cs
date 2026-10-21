using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class MediMigrationFixtureComparisonTests
{
    [Fact]
    public async Task StructuredMarkdownFixtureRecordsIntentionalMigrationDifferences()
    {
        const string markdown =
            """
            # Guide

            Intro.

            - first item
            - second item

            ```csharp
            Console.WriteLine("hello");
            ```
            """;
        FixtureChunk[] preMigration =
        [
            new("Guide\nIntro.", 1, "Guide", null, null, null),
            new(
                "Guide\n- first item - second item",
                6,
                "Guide",
                null,
                null,
                null),
            new(
                "Guide\n```csharp Console.WriteLine(\"hello\"); ```",
                3,
                "Guide",
                null,
                null,
                null),
        ];
        FixtureChunk[] expectedMedi =
        [
            new(
                string.Join(
                    '\n',
                    [
                        "Guide",
                        "Intro.",
                        string.Empty,
                        "first item",
                        "second item",
                        string.Empty,
                        "```csharp",
                        "Console.WriteLine(\"hello\");",
                        "```",
                    ]),
                3,
                "Guide",
                null,
                null,
                null),
        ];

        FixtureChunk[] actual = (await ChunkMarkdownAsync(markdown))
            .Select(ToFixture)
            .ToArray();

        AssertFixtureEqual(expectedMedi, actual);
        Assert.False(preMigration.SequenceEqual(actual));
    }

    [Fact]
    public async Task TimedTextFixtureMatchesPreMigrationTextCountsAndMetadata()
    {
        FixtureChunk[] preMigration =
        [
            new("one two three", 3, null, "en", 0, 30),
            new("four five", 2, null, "en", 40, 50),
        ];

        FixtureChunk[] actual = (await ChunkTimedTextAsync())
            .Select(ToFixture)
            .ToArray();

        AssertFixtureEqual(preMigration, actual);
    }

    private static async Task<IReadOnlyList<ContentChunk>> ChunkMarkdownAsync(
        string markdown)
    {
        WordCountTokenCounter counter = new();
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.Parse("96e3d636-b4ef-48a2-870d-42697a08c2c0"),
            Kind = ContentSourceKind.Pdf,
            Path = "fixture.pdf",
        };
        IngestionDocument document =
            await new MarkdownIngestionDocumentAdapter().ReadAsync(
                source,
                markdown,
                CancellationToken.None);
        return await new ContentChunkMapper(counter, maximumTokens: 100)
            .MapAsync(
                source,
                new HeadingAwareChunker(counter, maximumTokens: 100)
                    .ProcessAsync(document),
                CancellationToken.None);
    }

    private static async Task<IReadOnlyList<ContentChunk>>
        ChunkTimedTextAsync()
    {
        WordCountTokenCounter counter = new();
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.Parse("a24d4856-fec1-43e3-a4b9-a5945c7bc0a4"),
            Kind = ContentSourceKind.Subtitle,
            Path = "fixture.en.srt",
            Language = "en",
        };
        IngestionDocument document = TimedTextIngestionDocumentAdapter.Create(
            source,
            [
                new SubtitleCue(0, 10, "one two"),
                new SubtitleCue(20, 30, "three"),
                new SubtitleCue(40, 50, "four five"),
            ]);
        return await new ContentChunkMapper(counter, maximumTokens: 4)
            .MapAsync(
                source,
                new SubtitleChunker(counter, maximumTokens: 4)
                    .ProcessAsync(document),
                CancellationToken.None);
    }

    private static FixtureChunk ToFixture(ContentChunk chunk) => new(
        chunk.Text,
        new WordCountTokenCounter().CountTokens(chunk.Text),
        chunk.Heading,
        chunk.Language,
        chunk.StartTicks,
        chunk.EndTicks);

    private static void AssertFixtureEqual(
        IReadOnlyList<FixtureChunk> expected,
        IReadOnlyList<FixtureChunk> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Text, actual[index].Text);
            Assert.Equal(
                expected[index].ExactTokenCount,
                actual[index].ExactTokenCount);
            Assert.Equal(expected[index].Heading, actual[index].Heading);
            Assert.Equal(expected[index].Language, actual[index].Language);
            Assert.Equal(expected[index].StartTicks, actual[index].StartTicks);
            Assert.Equal(expected[index].EndTicks, actual[index].EndTicks);
        }
    }

    private sealed record FixtureChunk(
        string Text,
        int ExactTokenCount,
        string? Heading,
        string? Language,
        long? StartTicks,
        long? EndTicks);
}
