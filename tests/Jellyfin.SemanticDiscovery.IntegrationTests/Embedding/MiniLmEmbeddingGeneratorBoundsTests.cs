using Jellyfin.SemanticDiscovery.Core.Embedding;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

/// <summary>
/// Dedicated fixture with a small <see cref="MaxInputsPerRequest"/> so the
/// boundary tests exercise a real request-size rejection without needing to
/// build/tokenize dozens of throwaway inputs.
/// </summary>
public sealed class MiniLmBoundedGeneratorFixture : IDisposable
{
    public const int MaxInputsPerRequest = 2;

    public MiniLmBoundedGeneratorFixture()
    {
        if (MiniLmModelLocator.IsAvailable)
        {
            Generator = MiniLmModelFixture.CreateGenerator(
                maxInputsPerRequest: MaxInputsPerRequest,
                maxBatchSize: MaxInputsPerRequest);
        }
    }

    public MiniLmEmbeddingGenerator? Generator { get; }

    public void Dispose() => Generator?.Dispose();
}

/// <summary>
/// Verifies <see cref="MiniLmEmbeddingGenerator.GenerateAsync"/> enforces the
/// configured <see cref="MiniLmEmbeddingOptions.MaxInputsPerRequest"/> bound.
/// </summary>
public sealed class MiniLmEmbeddingGeneratorBoundsTests(MiniLmBoundedGeneratorFixture fixture)
    : IClassFixture<MiniLmBoundedGeneratorFixture>
{
    [ModelFact]
    public async Task GenerateAsyncRejectsRequestsExceedingMaxInputsPerRequest()
    {
        MiniLmEmbeddingGenerator generator = fixture.Generator!;
        string[] tooManyInputs = ["one", "two", "three"];

        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => generator.GenerateAsync(tooManyInputs));

        Assert.Equal("values", exception.ParamName);
        Assert.Contains(
            $"at most {MiniLmBoundedGeneratorFixture.MaxInputsPerRequest} inputs",
            exception.Message,
            StringComparison.Ordinal);
    }

    [ModelFact]
    public async Task GenerateAsyncAcceptsRequestAtExactlyMaxInputsPerRequest()
    {
        MiniLmEmbeddingGenerator generator = fixture.Generator!;
        string[] boundaryInputs = ["one", "two"];

        GeneratedEmbeddings<Embedding<float>> generated = await generator.GenerateAsync(boundaryInputs);

        Assert.Equal(MiniLmBoundedGeneratorFixture.MaxInputsPerRequest, generated.Count);
    }
}
