namespace Jellyfin.SemanticDiscovery.Core.Domain;

public static class SemanticConstants
{
    public const int EmbeddingDimensions = 384;
    public const int SnapshotSchemaVersion = 3;
    public const string ProjectionVersion =
        Jellyfin.SemanticDiscovery.Core.Projection
            .MetadataProjectionIdentity.ContractId;
    public const string EmbeddingAlgorithmVersion =
        "attention-mask-mean-pooling-l2-v1";
    public const string IndexingPipelineVersion =
        "srt-webvtt-v2;subtitle-chunker-v2;heading-chunker-v3;" +
        "wordpiece-scalar-bound-v2;markitdown-0.1.7-http-jsonrpc-v5;" +
        "locked-source-integrity-v2";
}
