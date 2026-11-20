using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.Core.Identity;

public static class OllamaIndexIdentity
{
    public static IndexIdentity Create(
        OllamaEmbeddingOptions options,
        string modelDigest,
        ApplicationChunkTokenizer tokenizer,
        string extractionProfile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDigest);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionProfile);

        return new IndexIdentity
        {
            ModelRevision = $"ollama:{options.Model}",
            ModelHash = modelDigest.ToLowerInvariant(),
            TokenizerRevision =
                $"app:{ApplicationChunkTokenizer.Model};" +
                $"ollama-tokenizer:{options.Model}",
            TokenizerHash = ApplicationChunkTokenizer.IdentityHash,
            EmbeddingAlgorithmVersion =
                $"{SemanticConstants.EmbeddingAlgorithmVersion};" +
                $"provider=ollama;dimensions={options.Dimensions}",
            IndexingPipelineVersion =
                $"{SemanticConstants.IndexingPipelineVersion};" +
                $"extraction={extractionProfile};" +
                $"chunking={SemanticConstants.DocumentChunkingIdentity}",
            ProjectionVersion = SemanticConstants.ProjectionVersion,
            ProjectionHash =
                ContentHash.Compute(SemanticConstants.ProjectionVersion),
        };
    }
}
