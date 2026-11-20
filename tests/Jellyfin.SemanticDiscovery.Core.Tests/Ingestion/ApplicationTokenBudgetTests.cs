using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class ApplicationTokenBudgetTests
{
    private static readonly WordCountTokenCounter Counter = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void SplitReturnsEmptyForBlankText(string text)
    {
        IReadOnlyList<string> parts =
            new ApplicationTokenBudget(Counter, maximumTokens: 10)
                .SplitNormalized(text);

        Assert.Empty(parts);
    }

    [Fact]
    public void SplitReturnsSinglePartWhenUnderBudget()
    {
        IReadOnlyList<string> parts =
            new ApplicationTokenBudget(Counter, maximumTokens: 5)
                .SplitNormalized("one two three");

        Assert.Equal(["one two three"], parts);
    }

    [Fact]
    public void SplitReturnsSinglePartWhenExactlyAtBudget()
    {
        IReadOnlyList<string> parts =
            new ApplicationTokenBudget(Counter, maximumTokens: 5)
                .SplitNormalized("one two three four five");

        Assert.Equal(["one two three four five"], parts);
    }

    [Fact]
    public void SplitProducesMultiplePartsEachWithinBudgetWhenOverBudget()
    {
        IReadOnlyList<string> parts =
            new ApplicationTokenBudget(Counter, maximumTokens: 3)
                .SplitNormalized("one two three four five six seven");

        Assert.Equal(["one two three", "four five six", "seven"], parts);
        Assert.All(parts, part => Assert.True(Counter.CountTokens(part) <= 3));
    }

    [Fact]
    public void SplitNormalizesWhitespaceBeforeCounting()
    {
        IReadOnlyList<string> parts =
            new ApplicationTokenBudget(Counter, maximumTokens: 10)
                .SplitNormalized("  one   two  \t three  ");

        Assert.Equal(["one two three"], parts);
    }

    [Fact]
    public void SplitThrowsWhenASingleWordAloneExceedsMaxTokens()
    {
        FixedTokenCounter counter = new(tokensPerCall: 100);

        TokenBudgetExceededException exception =
            Assert.Throws<TokenBudgetExceededException>(
            () => new ApplicationTokenBudget(counter, maximumTokens: 5)
                .SplitNormalized("word"));

        Assert.Contains("above the token limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitLargeManyWordInputUsesBoundedNearLinearTokenizationWork()
    {
        CountingTokenCounter counter = new();
        string input = string.Join(
            ' ',
            Enumerable.Range(0, 10_000).Select(index => $"word{index}"));

        IReadOnlyList<string> parts =
            new ApplicationTokenBudget(counter, maximumTokens: 50)
                .SplitNormalized(input);

        Assert.Equal(
            input,
            string.Join(' ', parts));
        Assert.All(
            parts,
            part => Assert.True(
                part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 50));
        Assert.True(counter.CharactersCounted < input.Length * 30L);
        Assert.True(counter.CallCount < 5_000);
    }

    private sealed class FixedTokenCounter(int tokensPerCall) : ITokenCounter
    {
        public int CountTokens(string text) => tokensPerCall;
    }

    private sealed class CountingTokenCounter : ITokenCounter
    {
        public int CallCount { get; private set; }

        public long CharactersCounted { get; private set; }

        public int CountTokens(string text)
        {
            CallCount++;
            CharactersCounted += text.Length;
            return text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }
}
