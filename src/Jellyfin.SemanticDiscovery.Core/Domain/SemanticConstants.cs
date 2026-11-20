namespace Jellyfin.SemanticDiscovery.Core.Domain;

public static class SemanticConstants
{
    public const int EmbeddingDimensions = 768;
    public const int MaximumChunkTokens = 384;
    public const string DocumentChunkingIdentity =
        "medi-header-chunker-v1;max-tokens=384;page-scoped";
    public const int SnapshotSchemaVersion = 3;
    public const string ProjectionVersion =
        Jellyfin.SemanticDiscovery.Core.Projection
            .MetadataProjectionIdentity.ContractId;
    public const string EmbeddingAlgorithmVersion =
        "ollama-embeddinggemma-v2";
    public const string IndexingPipelineVersion =
        "medi-11.0.0-preview.1.26560.3-documents-v2;" +
        "medi-chunks-v2;srt-webvtt-v4;subtitle-chunker-v4;" +
        "medi-header-chunker-v1;app-tiktoken-offsets-v1;" +
        "document-extraction-vlm-v1;locked-source-integrity-v3";
}
