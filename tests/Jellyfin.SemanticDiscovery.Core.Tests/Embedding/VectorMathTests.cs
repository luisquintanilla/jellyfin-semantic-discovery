using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Embedding;

public sealed class VectorMathTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void CosineSimilarityRejectsNonFiniteVectors(float invalid)
    {
        Assert.Throws<InvalidDataException>(
            () => VectorMath.CosineSimilarity(
                [1F, invalid],
                [1F, 0F]));
    }

    [Fact]
    public void CosineSimilarityReturnsZeroForZeroVector()
    {
        Assert.Equal(
            0F,
            VectorMath.CosineSimilarity([0F, 0F], [1F, 0F]));
    }
}
