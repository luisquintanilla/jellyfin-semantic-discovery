using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Indexing;

public sealed record ContentSourceDescriptor
{
    public required Guid OwnerId { get; init; }

    public required ContentSourceKind Kind { get; init; }

    public required string Path { get; init; }

    public string? Language { get; init; }
}

public sealed record IndexSourceItem
{
    public required MetadataRecord Metadata { get; init; }

    public IReadOnlyList<ContentSourceDescriptor> ContentSources { get; init; } = [];
}

public interface IIndexSource
{
    IAsyncEnumerable<IndexSourceItem> GetItemsAsync(
        CancellationToken cancellationToken);
}

public interface IContentReader
{
    bool Supports(ContentSourceKind kind);

    ValueTask<IReadOnlyList<ContentChunk>> ReadAsync(
        ContentSourceDescriptor source,
        CancellationToken cancellationToken);
}

public sealed class ContentIngestionException(
    string message,
    string sourcePath,
    bool isTransient,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string SourcePath { get; } = sourcePath;

    public bool IsTransient { get; } = isTransient;
}
