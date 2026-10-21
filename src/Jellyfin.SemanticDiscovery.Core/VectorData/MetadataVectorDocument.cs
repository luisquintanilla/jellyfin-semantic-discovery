using Jellyfin.SemanticDiscovery.Core.Domain;
using Microsoft.Extensions.VectorData;

namespace Jellyfin.SemanticDiscovery.Core.VectorData;

public sealed class MetadataVectorDocument
{
    [VectorStoreKey]
    public required Guid Id { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public required string Kind { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public required string ItemType { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public required string MediaType { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public required string Title { get; init; }

    [VectorStoreData]
    public string? OriginalTitle { get; init; }

    [VectorStoreData]
    public string? Overview { get; init; }

    [VectorStoreData]
    public int? ProductionYear { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public Guid? LibraryId { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public Guid? ParentId { get; init; }

    [VectorStoreData]
    public Guid[] AncestorIds { get; init; } = [];

    [VectorStoreData]
    public string? Path { get; init; }

    [VectorStoreData]
    public string[] Genres { get; init; } = [];

    [VectorStoreData]
    public string[] Tags { get; init; } = [];

    [VectorStoreData]
    public string[] People { get; init; } = [];

    [VectorStoreData]
    public string[] Studios { get; init; } = [];

    [VectorStoreData]
    public string? OfficialRating { get; init; }

    [VectorStoreData]
    public required string ProjectionHash { get; init; }

    [VectorStoreVector(
        SemanticConstants.EmbeddingDimensions,
        DistanceFunction = DistanceFunction.CosineSimilarity)]
    public required ReadOnlyMemory<float> Vector { get; init; }

    public MetadataRecord ToRecord() => new()
    {
        Id = Id,
        Kind = Enum.Parse<MediaKind>(Kind, ignoreCase: false),
        ItemType = ItemType,
        MediaType = MediaType,
        Title = Title,
        OriginalTitle = OriginalTitle,
        Overview = Overview,
        ProductionYear = ProductionYear,
        LibraryId = LibraryId,
        ParentId = ParentId,
        AncestorIds = [.. AncestorIds],
        Path = Path,
        Genres = [.. Genres],
        Tags = [.. Tags],
        People = [.. People],
        Studios = [.. Studios],
        OfficialRating = OfficialRating,
    };

    public static MetadataVectorDocument From(
        MetadataRecord record,
        string projectionHash,
        ReadOnlyMemory<float> vector) => new()
        {
            Id = record.Id,
            Kind = record.Kind.ToString(),
            ItemType = record.ItemType,
            MediaType = record.MediaType,
            Title = record.Title,
            OriginalTitle = record.OriginalTitle,
            Overview = record.Overview,
            ProductionYear = record.ProductionYear,
            LibraryId = record.LibraryId,
            ParentId = record.ParentId,
            AncestorIds = [.. record.AncestorIds],
            Path = record.Path,
            Genres = [.. record.Genres],
            Tags = [.. record.Tags],
            People = [.. record.People],
            Studios = [.. record.Studios],
            OfficialRating = record.OfficialRating,
            ProjectionHash = projectionHash,
            Vector = vector.ToArray(),
        };
}
