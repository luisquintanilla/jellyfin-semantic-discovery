using System.Security.Cryptography;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.Core.Identity;

public static class MiniLmIndexIdentity
{
    public const string Revision = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41";
    public const string ModelSha256 = MiniLmEmbeddingOptions.DefaultModelSha256;
    public const string VocabularySha256 = MiniLmEmbeddingOptions.DefaultVocabularySha256;

    internal static IndexIdentity Create(MiniLmEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        byte[] modelBytes = File.ReadAllBytes(options.ModelPath);
        byte[] vocabularyBytes = File.ReadAllBytes(options.VocabularyPath);
        return Create(options, modelBytes, vocabularyBytes);
    }

    internal static IndexIdentity Create(
        MiniLmEmbeddingOptions options,
        ReadOnlySpan<byte> modelBytes,
        ReadOnlySpan<byte> vocabularyBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        string modelHash = Convert.ToHexStringLower(SHA256.HashData(modelBytes));
        string vocabularyHash =
            Convert.ToHexStringLower(SHA256.HashData(vocabularyBytes));
        ValidateHash("model", modelHash, options.ExpectedModelSha256);
        ValidateHash("vocabulary", vocabularyHash, options.ExpectedVocabularySha256);

        return CreateFromValidatedHashes(options, modelHash, vocabularyHash);
    }

    internal static IndexIdentity CreateFromValidatedHashes(
        MiniLmEmbeddingOptions options,
        string modelHash,
        string vocabularyHash)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabularyHash);

        return new IndexIdentity
        {
            ModelRevision = options.ModelId,
            ModelHash = modelHash.ToLowerInvariant(),
            TokenizerRevision = $"Microsoft.ML.Tokenizers/2.0.0@{options.ModelId}",
            TokenizerHash = MiniLmTokenizerIdentity.ComputeHash(
                options.Tokenizer,
                options.MaxSequenceLength,
                vocabularyHash),
            EmbeddingAlgorithmVersion =
                $"{SemanticConstants.EmbeddingAlgorithmVersion};" +
                $"batch={options.MaxBatchSize};" +
                $"intra={options.IntraOpThreads};" +
                $"inter={options.InterOpThreads};" +
                $"concurrency={options.MaxConcurrentRuns}",
            IndexingPipelineVersion =
                SemanticConstants.IndexingPipelineVersion,
            ProjectionVersion = SemanticConstants.ProjectionVersion,
            ProjectionHash = ContentHash.Compute(SemanticConstants.ProjectionVersion),
        };
    }

    private static void ValidateHash(string artifact, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Pinned {artifact} checksum mismatch. Expected {expected}, found {actual}.");
        }
    }
}
