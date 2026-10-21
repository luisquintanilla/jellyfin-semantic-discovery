namespace Jellyfin.SemanticDiscovery.Core.Identity;

public sealed record IndexIdentity
{
    public required string ModelRevision { get; init; }

    public required string ModelHash { get; init; }

    public required string TokenizerRevision { get; init; }

    public required string TokenizerHash { get; init; }

    public required string EmbeddingAlgorithmVersion { get; init; }

    public required string IndexingPipelineVersion { get; init; }

    public required string ProjectionVersion { get; init; }

    public required string ProjectionHash { get; init; }

    public string CompositeHash =>
        ContentHash.Compute(
            string.Join(
                "\n",
                ModelRevision,
                ModelHash,
                TokenizerRevision,
                TokenizerHash,
                EmbeddingAlgorithmVersion,
                IndexingPipelineVersion,
                ProjectionVersion,
                ProjectionHash));
}
