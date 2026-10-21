namespace Jellyfin.SemanticDiscovery.Core.Domain;

public sealed record ContentChunk
{
    public required string Id { get; init; }

    public required Guid OwnerId { get; init; }

    public required ContentSourceKind SourceKind { get; init; }

    public required string SourcePath { get; init; }

    public required int Ordinal { get; init; }

    public required string Text { get; init; }

    public string? Language { get; init; }

    public long? StartTicks { get; init; }

    public long? EndTicks { get; init; }

    public string? Heading { get; init; }

    public required string ContentHash { get; init; }
}
