using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Snapshots;

namespace Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

/// <summary>
/// Small factory helpers that build fully-populated domain records with
/// sensible defaults, so individual tests only need to specify the fields
/// relevant to what they assert.
/// </summary>
public static class TestData
{
    public static MetadataRecord Movie(
        Guid? id = null,
        string title = "Sample Movie",
        string? overview = null,
        Guid? libraryId = null,
        Guid? parentId = null,
        Guid[]? ancestorIds = null,
        string itemType = "Movie",
        string mediaType = "Video",
        string[]? genres = null,
        string[]? tags = null,
        string[]? people = null,
        string[]? studios = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Kind = MediaKind.Movie,
            ItemType = itemType,
            MediaType = mediaType,
            Title = title,
            Overview = overview,
            LibraryId = libraryId ?? Guid.NewGuid(),
            ParentId = parentId ?? Guid.NewGuid(),
            AncestorIds = ancestorIds ?? [],
            Genres = genres ?? [],
            Tags = tags ?? [],
            People = people ?? [],
            Studios = studios ?? [],
        };

    public static MetadataRecord Book(
        Guid? id = null,
        string title = "Sample Book",
        string itemType = "Book") => new()
        {
            Id = id ?? Guid.NewGuid(),
            Kind = MediaKind.Book,
            ItemType = itemType,
            MediaType = "Book",
            Title = title,
            LibraryId = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
        };

    public static ContentChunk Chunk(
        Guid ownerId,
        string sourcePath = "sample.srt",
        ContentSourceKind sourceKind = ContentSourceKind.Subtitle,
        int ordinal = 0,
        string text = "sample chunk text",
        string? id = null,
        string? contentHash = null)
    {
        string resolvedHash = contentHash ?? ContentHash.Compute(text);
        return new ContentChunk
        {
            Id = id ?? ContentChunkIdentity.Compute(
                ownerId,
                sourcePath,
                sourceKind,
                ordinal,
                resolvedHash),
            OwnerId = ownerId,
            SourceKind = sourceKind,
            SourcePath = sourcePath,
            Ordinal = ordinal,
            Text = text,
            StartTicks = sourceKind is
                ContentSourceKind.Subtitle or ContentSourceKind.Transcript
                ? 0
                : null,
            EndTicks = sourceKind is
                ContentSourceKind.Subtitle or ContentSourceKind.Transcript
                ? TimeSpan.TicksPerSecond
                : null,
            ContentHash = resolvedHash,
        };
    }

    public static IndexIdentity Identity(string suffix = "1") => new()
    {
        ModelRevision = $"model-revision-{suffix}",
        ModelHash = ContentHash.Compute($"model-{suffix}"),
        TokenizerRevision = $"tokenizer-revision-{suffix}",
        TokenizerHash = ContentHash.Compute($"tokenizer-{suffix}"),
        EmbeddingAlgorithmVersion =
            SemanticConstants.EmbeddingAlgorithmVersion,
        IndexingPipelineVersion =
            SemanticConstants.IndexingPipelineVersion,
        ProjectionVersion = $"projection-version-{suffix}",
        ProjectionHash = ContentHash.Compute($"projection-{suffix}"),
    };

    public static IndexSnapshot Seal(IndexSnapshot snapshot) => snapshot with
    {
        VectorPayloadHash = IndexSnapshotVectorHash.Compute(
            snapshot.Metadata,
            snapshot.Content),
    };
}
