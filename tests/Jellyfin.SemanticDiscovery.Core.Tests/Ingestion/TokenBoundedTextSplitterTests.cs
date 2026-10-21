using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class TokenBoundedTextSplitterTests
{
    private static readonly WordCountTokenCounter Counter = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void SplitReturnsEmptyForBlankText(string text)
    {
        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(text, Counter, maxTokens: 10);

        Assert.Empty(parts);
    }

    [Fact]
    public void SplitReturnsSinglePartWhenUnderBudget()
    {
        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(
            "one two three",
            Counter,
            maxTokens: 5);

        Assert.Equal(["one two three"], parts);
    }

    [Fact]
    public void SplitReturnsSinglePartWhenExactlyAtBudget()
    {
        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(
            "one two three four five",
            Counter,
            maxTokens: 5);

        Assert.Equal(["one two three four five"], parts);
    }

    [Fact]
    public void SplitProducesMultiplePartsEachWithinBudgetWhenOverBudget()
    {
        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(
            "one two three four five six seven",
            Counter,
            maxTokens: 3);

        Assert.Equal(["one two three", "four five six", "seven"], parts);
        Assert.All(parts, part => Assert.True(Counter.CountTokens(part) <= 3));
    }

    [Fact]
    public void SplitNormalizesWhitespaceBeforeCounting()
    {
        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(
            "  one   two  \t three  ",
            Counter,
            maxTokens: 10);

        Assert.Equal(["one two three"], parts);
    }

    [Fact]
    public void SplitThrowsWhenASingleWordAloneExceedsMaxTokens()
    {
        FixedTokenCounter counter = new(tokensPerCall: 100);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => TokenBoundedTextSplitter.Split("word", counter, maxTokens: 5));

        Assert.Contains("above the token limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitLargeManyWordInputUsesBoundedNearLinearTokenizationWork()
    {
        CountingTokenCounter counter = new();
        string input = string.Join(
            ' ',
            Enumerable.Range(0, 10_000).Select(index => $"word{index}"));

        IReadOnlyList<string> parts = TokenBoundedTextSplitter.Split(
            input,
            counter,
            maxTokens: 50);

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
