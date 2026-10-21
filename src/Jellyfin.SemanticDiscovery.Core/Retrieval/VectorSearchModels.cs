using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

public sealed record MetadataMatch(
    MetadataRecord Record,
    float Similarity,
    int Rank);

public sealed record ContentMatch(
    ContentChunk Chunk,
    float Similarity,
    int Rank);

public sealed record SemanticSearchResult(
    Guid ItemId,
    MediaKind Kind,
    string Title,
    float Score);

public sealed record SimilarItemResult(
    Guid ItemId,
    MediaKind Kind,
    string Title,
    float Score);
