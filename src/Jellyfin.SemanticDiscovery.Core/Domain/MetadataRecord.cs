namespace Jellyfin.SemanticDiscovery.Core.Domain;

public sealed record MetadataRecord
{
    public required Guid Id { get; init; }

    public required MediaKind Kind { get; init; }

    public required string ItemType { get; init; }

    public required string MediaType { get; init; }

    public required string Title { get; init; }

    public string? OriginalTitle { get; init; }

    public string? Overview { get; init; }

    public int? ProductionYear { get; init; }

    public Guid? LibraryId { get; init; }

    public Guid? ParentId { get; init; }

    public Guid[] AncestorIds { get; init; } = [];

    public string? Path { get; init; }

    public string[] Genres { get; init; } = [];

    public string[] Tags { get; init; } = [];

    public string[] People { get; init; } = [];

    public string[] Studios { get; init; } = [];

    public string? OfficialRating { get; init; }
}
