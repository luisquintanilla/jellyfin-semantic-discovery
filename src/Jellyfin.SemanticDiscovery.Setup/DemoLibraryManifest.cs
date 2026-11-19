namespace Jellyfin.SemanticDiscovery.Setup;

public sealed record DemoLibraryManifest(
    string ProfileId,
    DemoLibrary[] Libraries,
    string? ArtworkStatus = null,
    DemoAsset[]? Assets = null);

public sealed record DemoAsset(
    string Id,
    string MediaKind,
    string Media,
    string? Artwork = null,
    string? ArtworkSha256 = null,
    string? Backdrop = null,
    string? BackdropSha256 = null);

public sealed record DemoLibrary(
    string Name,
    string CollectionType,
    string ItemType,
    string Path,
    DemoLibraryItem[] Items);

public sealed record DemoLibraryItem(
    string Title,
    string Type,
    string Path,
    string? Overview,
    string[]? Genres,
    string[]? Tags,
    string[]? Studios,
    string? OfficialRating);
