using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

public sealed class RealTokenizerTextSplitterTests
{
    [ModelFact]
    public void NoSpaceUnknownAndCjkInputsRespectTokenAndScalarBounds()
    {
        using MiniLmEmbeddingGenerator generator =
            MiniLmModelFixture.CreateGenerator();
        string latin = new('x', 350);
        string cjk = string.Concat(Enumerable.Repeat("漢", 350));

        AssertBounded(latin, generator);
        AssertBounded(cjk, generator);
    }

    private static void AssertBounded(
        string input,
        MiniLmEmbeddingGenerator generator)
    {
        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(
            input,
            generator,
            maxTokens: 256);

        Assert.True(parts.Count > 1);
        Assert.Equal(input, string.Concat(parts));
        Assert.All(
            parts,
            part =>
            {
                Assert.True(generator.CountTokens(part) <= 256);
                Assert.True(
                    part.EnumerateRunes().Count() <=
                    TokenBoundedTextSplitter.MaximumScalarsPerUnbrokenSegment);
            });
    }
}
