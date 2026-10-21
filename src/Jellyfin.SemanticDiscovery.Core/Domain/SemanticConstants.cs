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
        "medi-10.9.0-preview.1.26411.16-documents-v1;" +
        "medi-chunks-v1;srt-webvtt-v3;subtitle-chunker-v3;heading-chunker-v4;" +
        "wordpiece-scalar-bound-v3;markitdown-0.1.7-http-jsonrpc-v5;" +
        "locked-source-integrity-v2";
}
