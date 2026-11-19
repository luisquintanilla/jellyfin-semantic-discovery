using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Embedding;

public sealed class ApplicationChunkTokenizerTests
{
    [Fact]
    public void NormalizeWithSourceOffsetsCollapsesWhitespaceAndMapsSource()
    {
        ApplicationChunkTokenizer tokenizer = new();

        NormalizedText normalized =
            tokenizer.NormalizeWithSourceOffsets("  Alpha\tBeta");

        Assert.Equal("Alpha Beta", normalized.Text);
        Assert.Equal(2, normalized.GetSourceOffset(0));
        Assert.Equal(7, normalized.GetSourceOffset(5));
        Assert.Equal(8, normalized.GetSourceOffset(6));
    }

    [Fact]
    public void CountTokensUsesNormalizedApplicationText()
    {
        ApplicationChunkTokenizer tokenizer = new();

        Assert.Equal(
            tokenizer.CountTokens("Alpha Beta"),
            tokenizer.CountTokens("  Alpha\r\n\tBeta  "));
        Assert.NotEmpty(ApplicationChunkTokenizer.IdentityHash);
    }
}
