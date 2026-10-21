using Jellyfin.SemanticDiscovery.Core.Domain;
using Microsoft.Extensions.VectorData;

namespace Jellyfin.SemanticDiscovery.Core.VectorData;

public sealed class ContentVectorDocument
{
    [VectorStoreKey]
    public required string Id { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public required Guid OwnerId { get; init; }

    [VectorStoreData(IsIndexed = true)]
    public required string SourceKind { get; init; }

    [VectorStoreData]
    public required string SourcePath { get; init; }

    [VectorStoreData]
    public required int Ordinal { get; init; }

    [VectorStoreData]
    public required string Text { get; init; }

    [VectorStoreData]
    public string? Language { get; init; }

    [VectorStoreData]
    public long? StartTicks { get; init; }

    [VectorStoreData]
    public long? EndTicks { get; init; }

    [VectorStoreData]
    public string? Heading { get; init; }

    [VectorStoreData]
    public required string ContentHash { get; init; }

    [VectorStoreVector(
        SemanticConstants.EmbeddingDimensions,
        DistanceFunction = DistanceFunction.CosineSimilarity)]
    public required ReadOnlyMemory<float> Vector { get; init; }

    public ContentChunk ToChunk() => new()
    {
        Id = Id,
        OwnerId = OwnerId,
        SourceKind = Enum.Parse<ContentSourceKind>(SourceKind, ignoreCase: false),
        SourcePath = SourcePath,
        Ordinal = Ordinal,
        Text = Text,
        Language = Language,
        StartTicks = StartTicks,
        EndTicks = EndTicks,
        Heading = Heading,
        ContentHash = ContentHash,
    };

    public static ContentVectorDocument From(
        ContentChunk chunk,
        ReadOnlyMemory<float> vector) => new()
        {
            Id = chunk.Id,
            OwnerId = chunk.OwnerId,
            SourceKind = chunk.SourceKind.ToString(),
            SourcePath = chunk.SourcePath,
            Ordinal = chunk.Ordinal,
            Text = chunk.Text,
            Language = chunk.Language,
            StartTicks = chunk.StartTicks,
            EndTicks = chunk.EndTicks,
            Heading = chunk.Heading,
            ContentHash = chunk.ContentHash,
            Vector = vector.ToArray(),
        };
}
