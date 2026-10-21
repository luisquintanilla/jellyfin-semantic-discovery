using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

public sealed record SearchScope
{
    public Guid UserId { get; init; }

    public IReadOnlySet<MediaKind> Kinds { get; init; } =
        new HashSet<MediaKind>(Enum.GetValues<MediaKind>());

    public IReadOnlySet<string> IncludeItemTypes { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> ExcludeItemTypes { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> MediaTypes { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public Guid? ParentId { get; init; }

    public Guid? LibraryId { get; init; }

    public bool MatchesStructure(MetadataRecord item) =>
        Kinds.Contains(item.Kind) &&
        (IncludeItemTypes.Count == 0 || IncludeItemTypes.Contains(item.ItemType)) &&
        !ExcludeItemTypes.Contains(item.ItemType) &&
        (MediaTypes.Count == 0 || MediaTypes.Contains(item.MediaType)) &&
        (!ParentId.HasValue ||
         ParentId == Guid.Empty ||
         item.ParentId == ParentId ||
         item.AncestorIds.Contains(ParentId.Value)) &&
        (!LibraryId.HasValue || item.LibraryId == LibraryId);
}
