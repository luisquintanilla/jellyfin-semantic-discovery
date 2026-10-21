namespace Jellyfin.SemanticDiscovery.Core.Domain;

public sealed record SemanticMetadataSidecar
{
    public required int SchemaVersion { get; init; }

    public required string ProjectionHashContract { get; init; }

    public required MediaKind Kind { get; init; }

    public required string ItemType { get; init; }

    public required string MediaType { get; init; }

    public required string Title { get; init; }

    public required int ProductionYear { get; init; }

    public required string Overview { get; init; }

    public required string[] Genres { get; init; }

    public required string[] Tags { get; init; }

    public required string[] People { get; init; }

    public required string[] Studios { get; init; }

    public required string ProjectedMetadataSha256 { get; init; }

    public required SemanticSourceLock[] ContentSources { get; init; }
}

public sealed record SemanticSourceLock
{
    public required string FileName { get; init; }

    public required ContentSourceKind Kind { get; init; }

    public required long Size { get; init; }

    public required string Sha256 { get; init; }
}
