using System.Numerics.Tensors;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

/// <summary>
/// Verifies real-model output against the pinned embedded reference
/// (assets/minilm-parity.json): the full 384-float reference vector, the
/// pairwise cosine matrix, and actual (not self-referential) L2 norms.
/// </summary>
[Collection(MiniLmModelCollectionDefinition.Name)]
public sealed class MiniLmParityVerifierTests(MiniLmModelFixture fixture)
{
    [ModelFact]
    public async Task VerifyAsyncPassesPinnedVectorCosineAndNormTolerances()
    {
        MiniLmParityResult result = await MiniLmParityVerifier.VerifyAsync(fixture.Generator!);

        Assert.Equal(384, result.Dimensions);
        Assert.True(
            result.MaximumAbsoluteError <= 0.0005F,
            $"Maximum absolute vector error {result.MaximumAbsoluteError} exceeded 0.0005.");
        Assert.True(
            result.MaximumCosineError <= 0.0002F,
            $"Maximum pairwise cosine error {result.MaximumCosineError} exceeded 0.0002.");
        Assert.True(
            result.MaximumNormError <= 0.00001F,
            $"Maximum L2 norm error {result.MaximumNormError} exceeded 0.00001.");
    }

    [ModelFact]
    public async Task GenerateAsyncProducesVectorsWithActualUnitL2Norm()
    {
        GeneratedEmbeddings<Embedding<float>> generated = await fixture.Generator!.GenerateAsync(
            ["café Zürich", "cafe zurich", "the quiet cartouche hums twice before dawn"]);

        Assert.Equal(3, generated.Count);
        foreach (Embedding<float> embedding in generated)
        {
            // Measure the actual L2 norm of the returned vector directly. A
            // self-cosine comparison (CosineSimilarity(v, v)) would be
            // tautologically 1.0 regardless of the vector's true magnitude and
            // would not catch a normalization regression.
            float actualNorm = TensorPrimitives.Norm(embedding.Vector.Span);
            Assert.InRange(actualNorm, 0.9999F, 1.0001F);
        }
    }
}
