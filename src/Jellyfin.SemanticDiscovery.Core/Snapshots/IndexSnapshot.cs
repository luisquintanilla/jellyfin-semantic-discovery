using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Core.Snapshots;

public sealed record IndexSnapshot
{
    public int SchemaVersion { get; init; } = SemanticConstants.SnapshotSchemaVersion;

    public required IndexIdentity Identity { get; init; }

    public string VectorPayloadHash { get; init; } = string.Empty;

    public required DateTimeOffset CreatedUtc { get; init; }

    public required PersistedMetadataVector[] Metadata { get; init; }

    public required PersistedContentVector[] Content { get; init; }
}

public sealed record PersistedMetadataVector
{
    public required MetadataRecord Record { get; init; }

    public required string ProjectionHash { get; init; }

    public required float[] Vector { get; init; }
}

public sealed record PersistedContentVector
{
    public required ContentChunk Chunk { get; init; }

    public required float[] Vector { get; init; }
}
