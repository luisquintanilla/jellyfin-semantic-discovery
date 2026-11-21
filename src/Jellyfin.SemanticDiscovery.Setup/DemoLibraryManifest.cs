namespace Jellyfin.SemanticDiscovery.Setup;

/// <summary>The part of assets/demo/manifest.json that Setup needs: which libraries to create.</summary>
public sealed record DemoLibraryManifest(DemoLibrary[] Libraries);

public sealed record DemoLibrary(string Name, string CollectionType, string ItemType, string Path, DemoLibraryItem[] Items);

public sealed record DemoLibraryItem(string Title, string Type, string Path);
