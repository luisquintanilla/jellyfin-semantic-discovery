using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class ContentChunkMapperTests
{
    [Fact]
    public async Task MapCanonicalizesLineEndingsBeforeHashingAndIdentity()
    {
        WordCountTokenCounter counter = new();
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = "book.pdf",
        };

        ContentChunk lineFeed = Assert.Single(
            await MapAsync(source, "one\ntwo", counter));
        ContentChunk carriageReturnLineFeed = Assert.Single(
            await MapAsync(source, "one\r\ntwo", counter));
        ContentChunk carriageReturn = Assert.Single(
            await MapAsync(source, "one\rtwo", counter));

        Assert.Equal("one\ntwo", lineFeed.Text);
        Assert.Equal(lineFeed.Text, carriageReturnLineFeed.Text);
        Assert.Equal(lineFeed.Text, carriageReturn.Text);
        Assert.Equal(lineFeed.ContentHash, carriageReturnLineFeed.ContentHash);
        Assert.Equal(lineFeed.ContentHash, carriageReturn.ContentHash);
        Assert.Equal(lineFeed.Id, carriageReturnLineFeed.Id);
        Assert.Equal(lineFeed.Id, carriageReturn.Id);
    }

    [Fact]
    public async Task MapRejectsIncorrectExactTokenMetadata()
    {
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = "book.pdf",
        };
        IngestionDocument document = new(
            IngestionDocumentIdentity.Create(source));
        IngestionChunk<string> chunk = new("one two", document);
        chunk.Metadata[IngestionMetadata.TokenCount] = 1;

        InvalidDataException exception =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => new ContentChunkMapper(new WordCountTokenCounter())
                    .MapAsync(
                        source,
                        YieldAsync(chunk),
                        CancellationToken.None)
                    .AsTask());

        Assert.Contains(
            "exact model count",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static ValueTask<IReadOnlyList<ContentChunk>> MapAsync(
        ContentSourceDescriptor source,
        string content,
        WordCountTokenCounter counter)
    {
        IngestionDocument document = new(
            IngestionDocumentIdentity.Create(source));
        IngestionChunk<string> chunk = new(content, document);
        chunk.Metadata[IngestionMetadata.TokenCount] =
            counter.CountTokens(content.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal).Replace('\r', '\n'));
        return new ContentChunkMapper(counter).MapAsync(
            source,
            YieldAsync(chunk),
            CancellationToken.None);
    }

    private static async IAsyncEnumerable<IngestionChunk<string>> YieldAsync(
        IngestionChunk<string> chunk)
    {
        await Task.CompletedTask;
        yield return chunk;
    }
}
