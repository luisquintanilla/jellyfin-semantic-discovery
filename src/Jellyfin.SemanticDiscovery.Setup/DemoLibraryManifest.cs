namespace Jellyfin.SemanticDiscovery.Setup;

public sealed record DemoLibraryManifest(
    string ProfileId,
    DemoLibrary[] Libraries);

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
