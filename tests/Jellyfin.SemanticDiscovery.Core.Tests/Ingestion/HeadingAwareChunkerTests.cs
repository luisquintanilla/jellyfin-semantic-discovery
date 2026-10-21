using System.Reflection;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class HeadingAwareChunkerTests
{
    [Fact]
    public async Task ChunkBuildsHeadingAncestryPathFromNestedHeadings()
    {
        string markdown =
            "# Chapter 1\n\nIntro paragraph.\n\n" +
            "## Section A\n\nSection A content.\n";

        IReadOnlyList<ContentChunk> chunks =
            await ChunkAsync(markdown);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Chapter 1", chunks[0].Heading);
        Assert.Equal("Chapter 1 > Section A", chunks[1].Heading);
    }

    [Fact]
    public async Task ChunkResetsDeeperHeadingLevelsWhenShallowerHeadingAppears()
    {
        string markdown =
            "# Chapter 1\n\n## Section A\n\nFirst.\n\n" +
            "# Chapter 2\n\nSecond.\n";

        IReadOnlyList<ContentChunk> chunks =
            await ChunkAsync(markdown);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Chapter 1 > Section A", chunks[0].Heading);
        Assert.Equal("Chapter 2", chunks[1].Heading);
    }

    [Fact]
    public async Task ChunkProducesDeterministicIdsOrdinalsAndHashes()
    {
        Guid ownerId = Guid.NewGuid();
        const string markdown = "# Heading\n\nBody text.\n";

        IReadOnlyList<ContentChunk> first =
            await ChunkAsync(markdown, ownerId: ownerId);
        IReadOnlyList<ContentChunk> second =
            await ChunkAsync(markdown, ownerId: ownerId);

        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[0].ContentHash, second[0].ContentHash);
        Assert.Equal(0, first[0].Ordinal);
    }

    [Fact]
    public async Task ChunkProducesDifferentIdsForDifferentContent()
    {
        Guid ownerId = Guid.NewGuid();

        IReadOnlyList<ContentChunk> first =
            await ChunkAsync("# H\n\nOne.\n", ownerId: ownerId);
        IReadOnlyList<ContentChunk> second =
            await ChunkAsync("# H\n\nTwo.\n", ownerId: ownerId);

        Assert.NotEqual(first[0].Id, second[0].Id);
        Assert.NotEqual(first[0].ContentHash, second[0].ContentHash);
    }

    [Fact]
    public async Task ChunkIdentityIsStableAcrossMarkdownLineEndings()
    {
        Guid ownerId = Guid.NewGuid();
        const string lineFeedMarkdown =
            "# Heading\n\nParagraph.\n\n```text\ncode\n```\n";

        IReadOnlyList<ContentChunk> lineFeed = await ChunkAsync(
            lineFeedMarkdown,
            ownerId: ownerId);
        IReadOnlyList<ContentChunk> carriageReturnLineFeed = await ChunkAsync(
            lineFeedMarkdown.Replace(
                "\n",
                "\r\n",
                StringComparison.Ordinal),
            ownerId: ownerId);

        Assert.Equal(
            lineFeed.Select(static chunk => chunk.Text),
            carriageReturnLineFeed.Select(static chunk => chunk.Text));
        Assert.Equal(
            lineFeed.Select(static chunk => chunk.ContentHash),
            carriageReturnLineFeed.Select(static chunk => chunk.ContentHash));
        Assert.Equal(
            lineFeed.Select(static chunk => chunk.Id),
            carriageReturnLineFeed.Select(static chunk => chunk.Id));
    }

    [Fact]
    public async Task ChunkPreservesStructuredMarkdownContentAndDomainIdentity()
    {
        Guid ownerId = Guid.NewGuid();
        const string sourcePath = "guide.pdf";
        string markdown =
            """
            # Guide

            Intro paragraph.

            - first item
            - second item

            | Name | Value |
            | --- | --- |
            | alpha | one |

            ```csharp
            Console.WriteLine("hello");
            ```

            <div data-example="raw">Raw HTML</div>
            """;

        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            markdown,
            ownerId,
            sourcePath,
            ContentSourceKind.Pdf,
            maxTokens: 200);

        Assert.NotEmpty(chunks);
        Assert.Equal(
            Enumerable.Range(0, chunks.Count),
            chunks.Select(static chunk => chunk.Ordinal));
        Assert.All(
            chunks,
            chunk =>
            {
                Assert.Equal(ownerId, chunk.OwnerId);
                Assert.Equal(sourcePath, chunk.SourcePath);
                Assert.Equal(ContentSourceKind.Pdf, chunk.SourceKind);
                Assert.Equal("Guide", chunk.Heading);
                Assert.Equal(ContentHash.Compute(chunk.Text), chunk.ContentHash);
                Assert.Equal(
                    ContentChunkIdentity.Compute(
                        ownerId,
                        sourcePath,
                        ContentSourceKind.Pdf,
                        chunk.Ordinal,
                        chunk.ContentHash),
                    chunk.Id);
            });

        string projected = string.Join(
            '\n',
            chunks.Select(static chunk => chunk.Text));
        Assert.Contains("Intro paragraph.", projected, StringComparison.Ordinal);
        Assert.Contains(
            "first item\nsecond item",
            projected,
            StringComparison.Ordinal);
        Assert.Contains("| Name | Value |", projected, StringComparison.Ordinal);
        Assert.Contains(
            "Console.WriteLine(\"hello\");",
            projected,
            StringComparison.Ordinal);
        Assert.Contains(
            "<div data-example=\"raw\">Raw HTML</div>",
            projected,
            StringComparison.Ordinal);
        Assert.True(
            projected.IndexOf("Intro paragraph.", StringComparison.Ordinal) <
            projected.IndexOf("first item", StringComparison.Ordinal));
        Assert.True(
            projected.IndexOf("first item", StringComparison.Ordinal) <
            projected.IndexOf("| Name | Value |", StringComparison.Ordinal));
        Assert.True(
            projected.IndexOf("| Name | Value |", StringComparison.Ordinal) <
            projected.IndexOf("Console.WriteLine", StringComparison.Ordinal));
        Assert.True(
            projected.IndexOf("Console.WriteLine", StringComparison.Ordinal) <
            projected.IndexOf("<div data-example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkFallsBackForUnsupportedAdvancedMarkdown()
    {
        string markdown =
            """
            # Advanced

            - [x] completed task

            A sentence with a footnote.[^1]

            [^1]: retained footnote detail
            """;

        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            markdown,
            maxTokens: 200);

        string projected = string.Join(
            '\n',
            chunks.Select(static chunk => chunk.Text));
        Assert.Contains(
            "completed task",
            projected,
            StringComparison.Ordinal);
        Assert.Contains(
            "A sentence with a footnote.",
            projected,
            StringComparison.Ordinal);
        Assert.Contains(
            "retained footnote detail",
            projected,
            StringComparison.Ordinal);
        Assert.All(
            chunks,
            chunk => Assert.True(
                new WordCountTokenCounter().CountTokens(chunk.Text) <= 200));
    }

    [Fact]
    public async Task DocumentAdapterDoesNotReplaceCancellationWithFallback()
    {
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = "advanced.pdf",
        };
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MarkdownIngestionDocumentAdapter()
                .ReadAsync(
                    source,
                    "- [x] completed task",
                    cancellationSource.Token)
                .AsTask());
    }

    [Fact]
    public async Task ChunkFallsBackWhenMediDropsNestedListAndCodeContent()
    {
        string markdown =
            """
            # Nested

            - parent
              - duplicate item
              - duplicate item

                ```csharp
                Console.WriteLine("nested");
                ```

            After the nested list.
            """;

        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            markdown,
            maxTokens: 200);

        string projected = string.Join(
            '\n',
            chunks.Select(static chunk => chunk.Text));
        Assert.Equal(
            2,
            CountOccurrences(projected, "duplicate item"));
        Assert.Contains(
            "Console.WriteLine(\"nested\");",
            projected,
            StringComparison.Ordinal);
        Assert.Contains(
            "After the nested list.",
            projected,
            StringComparison.Ordinal);
        Assert.True(
            projected.IndexOf("parent", StringComparison.Ordinal) <
            projected.IndexOf(
                "Console.WriteLine",
                StringComparison.Ordinal));
        Assert.True(
            projected.IndexOf(
                "Console.WriteLine",
                StringComparison.Ordinal) <
            projected.IndexOf(
                "After the nested list.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkPreservesMixedAndMultipleImageParagraphTextWithoutLocations()
    {
        string markdown =
            """
            # Images

            Before ![diagram](https://private.example/diagram.png) Important explanation ![second](data:image/png;base64,AAAA) after.

            <img src="https://private.example/third.png" alt="third diagram"> Final explanation.
            """;

        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            markdown,
            maxTokens: 200);

        string projected = string.Join(
            '\n',
            chunks.Select(static chunk => chunk.Text));
        Assert.Contains(
            "Before diagram Important explanation second after.",
            projected,
            StringComparison.Ordinal);
        Assert.Contains(
            "third diagram Final explanation.",
            projected,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private.example",
            projected,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data:image",
            projected,
            StringComparison.Ordinal);
        Assert.True(
            projected.IndexOf("diagram", StringComparison.Ordinal) <
            projected.IndexOf(
                "Important explanation",
                StringComparison.Ordinal));
        Assert.True(
            projected.IndexOf(
                "Important explanation",
                StringComparison.Ordinal) <
            projected.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkRespectsTokenBudgetIncludingHeadingContext()
    {
        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            "## Head One\n\na b c\n",
            sourcePath: "book.epub",
            sourceKind: ContentSourceKind.Epub,
            maxTokens: 3);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.Equal("Head One", chunk.Heading));
        Assert.All(
            chunks,
            chunk => Assert.True(
                chunk.Text.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries).Length <= 3));
        Assert.All(
            chunks,
            chunk => Assert.StartsWith(
                "Head One",
                chunk.Text,
                StringComparison.Ordinal));

        string[] recombinedBodyWords = chunks
            .SelectMany(chunk => chunk.Text.Split('\n', 2)[1]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        Assert.Equal(["a", "b", "c"], recombinedBodyWords);
    }

    [Fact]
    public async Task ChunkSplitsOversizedSectionWithoutHeading()
    {
        IReadOnlyList<ContentChunk> chunks = await ChunkAsync(
            "word1 word2 word3 word4 word5 word6 word7\n",
            maxTokens: 3);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("word1 word2 word3", chunks[0].Text);
        Assert.Equal("word4 word5 word6", chunks[1].Text);
        Assert.Equal("word7", chunks[2].Text);
        Assert.All(chunks, chunk => Assert.Null(chunk.Heading));
    }

    [Fact]
    public async Task ChunkShrinksExtremeHeadingAgainstCombinedTokenCount()
    {
        SpecialTokenCounter counter = new();
        ContentChunk chunk = Assert.Single(await ChunkAsync(
            "# one two three four five six seven eight\n\nbody\n",
            sourcePath: "book.epub",
            sourceKind: ContentSourceKind.Epub,
            maxTokens: 6,
            tokenCounter: counter));

        Assert.Equal("body", chunk.Text.Split('\n')[^1]);
        Assert.False(string.IsNullOrWhiteSpace(chunk.Heading));
        Assert.True(counter.CountTokens(chunk.Text) <= 6);
    }

    [Fact]
    public async Task ChunkRetainsDeepestHeadingWhenAncestryExceedsBudget()
    {
        string ancestor = string.Join(
            ' ',
            Enumerable.Repeat("ancestor", 20));

        ContentChunk chunk = Assert.Single(await ChunkAsync(
            $"# {ancestor}\n\n## Specific\n\nbody\n",
            maxTokens: 2));

        Assert.Equal("Specific", chunk.Heading);
        Assert.Equal("Specific\nbody", chunk.Text);
    }

    [Fact]
    public async Task ChunkDropsContextWhenOneHeadingScalarExceedsBudget()
    {
        ContentChunk chunk = Assert.Single(await ChunkAsync(
            "# 💣\n\nbody\n",
            maxTokens: 2,
            tokenCounter: new RejectedHeadingTokenCounter()));

        Assert.Null(chunk.Heading);
        Assert.Equal("body", chunk.Text);
    }

    [Fact]
    public async Task ChunkPreservesCodeOnlyDocument()
    {
        ContentChunk chunk = Assert.Single(await ChunkAsync(
            "```csharp\nConsole.WriteLine(\"hello\");\n```\n",
            maxTokens: 50));

        Assert.Null(chunk.Heading);
        Assert.Contains(
            "Console.WriteLine(\"hello\");",
            chunk.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkRecordsExactModelTokenCountInMediMetadata()
    {
        WordCountTokenCounter counter = new();
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = "book.pdf",
        };
        IngestionDocument document =
            await new MarkdownIngestionDocumentAdapter().ReadAsync(
                source,
                "# Heading\n\none two three",
                CancellationToken.None);

        IReadOnlyList<IngestionChunk<string>> chunks = await CollectAsync(
            new HeadingAwareChunker(counter).ProcessAsync(document));

        Assert.All(
            chunks,
            chunk => Assert.Equal(
                counter.CountTokens(chunk.Content),
                Assert.IsType<int>(
                    chunk.Metadata[IngestionMetadata.TokenCount])));
    }

    [Fact]
    public async Task DocumentIdentifiersAreStableAndDoNotExposeSourcePaths()
    {
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.Parse("c7d4b4af-14ef-498a-aaba-c3f5f2c819ac"),
            Kind = ContentSourceKind.Epub,
            Path = @"C:\Users\private\Books\guide.epub",
        };
        MarkdownIngestionDocumentAdapter adapter = new();

        IngestionDocument first = await adapter.ReadAsync(
            source,
            "text",
            CancellationToken.None);
        IngestionDocument second = await adapter.ReadAsync(
            source,
            "different text",
            CancellationToken.None);
        IngestionDocument other = await adapter.ReadAsync(
            source with { Path = @"C:\Users\private\Books\other.epub" },
            "text",
            CancellationToken.None);

        Assert.Equal(first.Identifier, second.Identifier);
        Assert.NotEqual(first.Identifier, other.Identifier);
        Assert.StartsWith(
            "semantic-content:",
            first.Identifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private",
            first.Identifier,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "guide.epub",
            first.Identifier,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n   \n")]
    [InlineData("# Heading Only\n\n")]
    public async Task ChunkReturnsEmptyForBlankOrHeadingOnlyMarkdown(
        string markdown)
    {
        IReadOnlyList<ContentChunk> chunks =
            await ChunkAsync(markdown);

        Assert.Empty(chunks);
    }

    [Theory]
    [InlineData(ContentSourceKind.Subtitle)]
    [InlineData(ContentSourceKind.Transcript)]
    public async Task DocumentAdapterRejectsUnsupportedSourceKind(
        ContentSourceKind kind)
    {
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = kind,
            Path = "book.pdf",
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new MarkdownIngestionDocumentAdapter()
                .ReadAsync(source, "text", CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public void ConstructorOnlyDependsOnTokenCounterAndBudget()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(HeadingAwareChunker).GetConstructors());
        ParameterInfo[] parameters = constructor.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(ITokenCounter), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
    }

    private static async Task<IReadOnlyList<ContentChunk>> ChunkAsync(
        string markdown,
        Guid? ownerId = null,
        string sourcePath = "book.pdf",
        ContentSourceKind sourceKind = ContentSourceKind.Pdf,
        int maxTokens = 50,
        ITokenCounter? tokenCounter = null)
    {
        ITokenCounter counter = tokenCounter ?? new WordCountTokenCounter();
        ContentSourceDescriptor source = new()
        {
            OwnerId = ownerId ?? Guid.NewGuid(),
            Kind = sourceKind,
            Path = sourcePath,
        };
        IngestionDocument document =
            await new MarkdownIngestionDocumentAdapter().ReadAsync(
                source,
                markdown,
                CancellationToken.None);
        return await new ContentChunkMapper(counter, maxTokens).MapAsync(
            source,
            new HeadingAwareChunker(counter, maxTokens).ProcessAsync(document),
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

    private static int CountOccurrences(
        string value,
        string substring)
    {
        int count = 0;
        int position = 0;
        while ((position = value.IndexOf(
                   substring,
                   position,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += substring.Length;
        }

        return count;
    }

    private sealed class SpecialTokenCounter : ITokenCounter
    {
        public int CountTokens(string text) =>
            TextNormalization.NormalizeWhitespace(text)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Length + 2;
    }

    private sealed class RejectedHeadingTokenCounter : ITokenCounter
    {
        public int CountTokens(string text) =>
            text.Contains("💣", StringComparison.Ordinal)
                ? 100
                : TextNormalization.NormalizeWhitespace(text)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Length;
    }
}
