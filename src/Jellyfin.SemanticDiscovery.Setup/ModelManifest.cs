using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Setup;

public sealed record ModelManifest
{
    public const string PinnedRepository =
        "sentence-transformers/all-MiniLM-L6-v2";
    public const string PinnedLicenseUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/blob/" +
        "1110a243fdf4706b3f48f1d95db1a4f5529b4d41/README.md";
    public const long PinnedModelSize = 90_405_214;
    public const long PinnedVocabularySize = 231_508;

    public required int SchemaVersion { get; init; }

    public required string Repository { get; init; }

    public required string Revision { get; init; }

    public required string License { get; init; }

    public required string LicenseUrl { get; init; }

    public required int EmbeddingDimensions { get; init; }

    public required int MaxSequenceLength { get; init; }

    public required TokenizerManifest Tokenizer { get; init; }

    public required IReadOnlyList<ModelFile> Files { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 ||
            !string.Equals(
                Repository,
                PinnedRepository,
                StringComparison.Ordinal) ||
            !string.Equals(Revision, MiniLmIndexIdentity.Revision, StringComparison.Ordinal) ||
            EmbeddingDimensions != 384 ||
            MaxSequenceLength != 256 ||
            !string.Equals(License, "Apache-2.0", StringComparison.Ordinal) ||
            !string.Equals(
                LicenseUrl,
                PinnedLicenseUrl,
                StringComparison.Ordinal) ||
            Tokenizer is null ||
            !string.Equals(
                Tokenizer.IdentitySha256,
                MiniLmTokenizerIdentity.Hash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                $"{Tokenizer.Implementation};vocab-sha256=" +
                $"{MiniLmIndexIdentity.VocabularySha256};{Tokenizer.EffectiveOptions}",
                MiniLmTokenizerIdentity.EffectiveConfiguration,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The model manifest does not describe the pinned MiniLM artifact.");
        }

        if (Files is null ||
            Files.Count != 2 ||
            Files.Any(static file => file is null) ||
            Files.Select(static file => file.Path)
                .Distinct(StringComparer.Ordinal)
                .Count() != 2)
        {
            throw new InvalidDataException(
                "The model manifest must contain exactly two unique pinned files.");
        }

        ValidateFile(
            "model.onnx",
            "onnx/model.onnx",
            PinnedModelSize,
            MiniLmEmbeddingOptions.DefaultModelSha256);
        ValidateFile(
            "vocab.txt",
            "vocab.txt",
            PinnedVocabularySize,
            MiniLmEmbeddingOptions.DefaultVocabularySha256);
    }

    private void ValidateFile(
        string path,
        string sourcePath,
        long expectedSize,
        string expectedHash)
    {
        ModelFile? file = Files.SingleOrDefault(
            candidate => string.Equals(candidate.Path, path, StringComparison.Ordinal));
        if (file is null ||
            !string.Equals(
                file.SourcePath,
                sourcePath,
                StringComparison.Ordinal) ||
            !string.Equals(file.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase) ||
            file.Size != expectedSize)
        {
            throw new InvalidDataException($"The model manifest entry '{path}' is invalid.");
        }
    }
}

public sealed record TokenizerManifest
{
    public required string Implementation { get; init; }

    public required string EffectiveOptions { get; init; }

    public required string IdentitySha256 { get; init; }
}

public sealed record ModelFile
{
    public required string Path { get; init; }

    public required string SourcePath { get; init; }

    public required long Size { get; init; }

    public required string Sha256 { get; init; }
}
