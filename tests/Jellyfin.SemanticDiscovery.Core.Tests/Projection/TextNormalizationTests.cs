using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Projection;

public sealed class TextNormalizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void NormalizeWhitespaceReturnsEmptyForNullOrBlank(string? value)
    {
        Assert.Equal(string.Empty, TextNormalization.NormalizeWhitespace(value));
    }

    [Fact]
    public void NormalizeWhitespaceCollapsesInternalRunsAndTrims()
    {
        Assert.Equal(
            "a b c",
            TextNormalization.NormalizeWhitespace("  a   b\t\tc  "));
    }

    [Fact]
    public void NormalizeWhitespaceAppliesFormKCCompatibilityNormalization()
    {
        // U+FF41 FULLWIDTH LATIN SMALL LETTER A normalizes to U+0061 under FormKC.
        Assert.Equal("a", TextNormalization.NormalizeWhitespace("\uFF41"));
    }

    [Fact]
    public void NormalizeForIdentityUpperCasesAfterNormalizingWhitespace()
    {
        Assert.Equal(
            "THE MATRIX",
            TextNormalization.NormalizeForIdentity("  the   matrix  "));
    }

    [Fact]
    public void NormalizeForIdentityTreatsCaseAndWhitespaceVariantsAsEqual()
    {
        Assert.Equal(
            TextNormalization.NormalizeForIdentity("The Matrix"),
            TextNormalization.NormalizeForIdentity("  the    MATRIX "));
    }
}
