namespace Jellyfin.SemanticDiscovery.Core.Indexing;

public sealed record IndexBuildResult(
    int MetadataCount,
    int ContentChunkCount,
    int EmbeddedMetadataCount,
    int EmbeddedContentChunkCount,
    IReadOnlyList<ContentIngestionError> Errors);

public sealed record ContentIngestionError(
    string SourcePath,
    string Message,
    bool IsTransient);
