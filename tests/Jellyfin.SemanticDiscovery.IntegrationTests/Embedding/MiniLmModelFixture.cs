using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

/// <summary>
/// Shares a single loaded <see cref="MiniLmEmbeddingGenerator"/> (backed by the
/// pinned real ONNX model and vocabulary) across the read-only parity and
/// tokenization tests in the <see cref="MiniLmModelCollection"/>, avoiding a
/// separate ~90 MB model load per test class.
/// </summary>
public sealed class MiniLmModelFixture : IDisposable
{
    public MiniLmModelFixture()
    {
        if (MiniLmModelLocator.IsAvailable)
        {
            Generator = new MiniLmEmbeddingGenerator(
                CreateOptions(MiniLmModelLocator.ModelDirectory!));
        }
    }

    /// <summary>
    /// Gets the shared read-only generator, or <see langword="null"/> when the
    /// pinned model assets are unavailable. Tests guarded by
    /// <see cref="ModelFactAttribute"/> only run when this is non-null.
    /// </summary>
    public MiniLmEmbeddingGenerator? Generator { get; }

    /// <summary>
    /// Creates a dedicated (non-shared) generator instance for tests that need
    /// their own lifetime control, such as bounded-request or dispose/cancellation
    /// races that must not disturb the shared <see cref="Generator"/>.
    /// </summary>
    public static MiniLmEmbeddingGenerator CreateGenerator(
        int maxInputsPerRequest = 64,
        int maxBatchSize = 8,
        int maxConcurrentRuns = 1,
        TimeSpan? disposeTimeout = null)
    {
        if (!MiniLmModelLocator.IsAvailable)
        {
            throw new InvalidOperationException(MiniLmModelLocator.SkipReason);
        }

        MiniLmEmbeddingOptions options = CreateOptions(MiniLmModelLocator.ModelDirectory!) with
        {
            MaxInputsPerRequest = maxInputsPerRequest,
            MaxBatchSize = maxBatchSize,
            MaxConcurrentRuns = maxConcurrentRuns,
            DisposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(30),
        };
        return new MiniLmEmbeddingGenerator(options);
    }

    public void Dispose() => Generator?.Dispose();

    private static MiniLmEmbeddingOptions CreateOptions(string modelDirectory) => new()
    {
        ModelPath = Path.Combine(modelDirectory, "model.onnx"),
        VocabularyPath = Path.Combine(modelDirectory, "vocab.txt"),
    };
}

[CollectionDefinition(Name)]
public sealed class MiniLmModelCollectionDefinition : ICollectionFixture<MiniLmModelFixture>
{
    public const string Name = "MiniLM shared model";
}
