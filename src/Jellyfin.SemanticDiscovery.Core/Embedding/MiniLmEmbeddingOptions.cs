using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public sealed record MiniLmEmbeddingOptions
{
    public const string DefaultModelId =
        "sentence-transformers/all-MiniLM-L6-v2@1110a243fdf4706b3f48f1d95db1a4f5529b4d41";

    public const string DefaultModelSha256 =
        "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452";

    public const string DefaultVocabularySha256 =
        "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    public required string ModelPath { get; init; }

    public required string VocabularyPath { get; init; }

    public string ModelId { get; init; } = DefaultModelId;

    public string ExpectedModelSha256 { get; init; } = DefaultModelSha256;

    public string ExpectedVocabularySha256 { get; init; } = DefaultVocabularySha256;

    public MiniLmTokenizerSettings Tokenizer { get; init; } = new();

    public int Dimensions { get; init; } = SemanticConstants.EmbeddingDimensions;

    public int MaxSequenceLength { get; init; } = 256;

    public int MaxBatchSize { get; init; } = 8;

    public int MaxInputsPerRequest { get; init; } = 64;

    public int MaxConcurrentRuns { get; init; } = 1;

    public int IntraOpThreads { get; init; } = 1;

    public int InterOpThreads { get; init; } = 1;

    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(VocabularyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExpectedModelSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExpectedVocabularySha256);
        ArgumentNullException.ThrowIfNull(Tokenizer);

        if (!File.Exists(ModelPath))
        {
            throw new FileNotFoundException("The pinned ONNX model was not found.", ModelPath);
        }

        if (!File.Exists(VocabularyPath))
        {
            throw new FileNotFoundException(
                "The pinned WordPiece vocabulary was not found.",
                VocabularyPath);
        }

        if (Dimensions != SemanticConstants.EmbeddingDimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Dimensions),
                $"all-MiniLM-L6-v2 requires {SemanticConstants.EmbeddingDimensions} dimensions.");
        }

        if (MaxSequenceLength is < 4 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSequenceLength),
                "Sentence Transformers parity requires a sequence limit no greater than 256.");
        }

        if (MaxBatchSize < 1 || MaxInputsPerRequest < MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBatchSize),
                "Batch size must be positive and no larger than the request limit.");
        }

        if (MaxConcurrentRuns < 1 || IntraOpThreads < 1 || InterOpThreads < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentRuns),
                "Concurrency and ONNX thread counts must be positive.");
        }

        if (DisposeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DisposeTimeout),
                "The disposal drain timeout must be positive.");
        }
    }
}
