using System.Text;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal sealed class ApplicationTokenBudget
{
    internal const int MaximumInputTokens = SemanticConstants.MaximumChunkTokens;
    internal const int MaximumScalarsPerUnbrokenSegment = 100;

    private readonly ITokenCounter _tokenCounter;

    public ApplicationTokenBudget(
        ITokenCounter tokenCounter,
        int maximumTokens = MaximumInputTokens)
    {
        _tokenCounter =
            tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        MaximumTokens = maximumTokens > 0
            ? maximumTokens
            : throw new ArgumentOutOfRangeException(nameof(maximumTokens));
    }

    public int MaximumTokens { get; }

    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _tokenCounter.CountTokens(text);
    }

    public bool Fits(string text) =>
        CountTokens(text) <= MaximumTokens;

    public void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int tokenCount = CountTokens(text);
        if (tokenCount > MaximumTokens)
        {
            throw new TokenBudgetExceededException(
                $"The final chunk contains {tokenCount} tokens, above the " +
                $"{MaximumTokens}-token model limit.");
        }
    }

    public IReadOnlyList<string> SplitNormalized(
        string text,
        int? maximumTokens = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        int limit = maximumTokens ?? MaximumTokens;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        string normalized = TextNormalization.NormalizeWhitespace(text);
        if (normalized.Length == 0)
        {
            return [];
        }

        if (_tokenCounter.CountTokens(normalized) <= limit &&
            !ContainsOversizedSegment(normalized))
        {
            return [normalized];
        }

        string[] words = normalized.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        List<string> parts = [];
        int start = 0;

        while (start < words.Length)
        {
            int best = start;
            int firstInvalid = words.Length + 1;
            int step = 1;
            while (true)
            {
                int probe = Math.Min(words.Length, start + step);
                string candidate = string.Join(' ', words[start..probe]);
                if (!ContainsOversizedSegment(candidate) &&
                    _tokenCounter.CountTokens(candidate) <= limit)
                {
                    best = probe;
                    if (probe == words.Length)
                    {
                        break;
                    }

                    step = checked(step * 2);
                }
                else
                {
                    firstInvalid = probe;
                    break;
                }
            }

            if (best == start)
            {
                parts.AddRange(SplitOversizedSegment(
                    words[start],
                    limit));
                start++;
                continue;
            }

            if (firstInvalid <= words.Length)
            {
                int low = best + 1;
                int high = firstInvalid - 1;
                while (low <= high)
                {
                    int middle = low + ((high - low) / 2);
                    string candidate =
                        string.Join(' ', words[start..middle]);
                    if (!ContainsOversizedSegment(candidate) &&
                        _tokenCounter.CountTokens(candidate) <= limit)
                    {
                        best = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }
            }

            parts.Add(string.Join(' ', words[start..best]));
            start = best;
        }

        return parts;
    }

    private List<string> SplitOversizedSegment(
        string segment,
        int maximumTokens)
    {
        List<int> boundaries = [0];
        int offset = 0;
        foreach (Rune rune in segment.EnumerateRunes())
        {
            offset += rune.Utf16SequenceLength;
            boundaries.Add(offset);
        }

        List<string> parts = [];
        int start = 0;
        while (start < boundaries.Count - 1)
        {
            int low = start + 1;
            int high = Math.Min(
                boundaries.Count - 1,
                start + MaximumScalarsPerUnbrokenSegment);
            int best = start;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                string candidate = segment[
                    boundaries[start]..boundaries[middle]];
                if (_tokenCounter.CountTokens(candidate) <= maximumTokens)
                {
                    best = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (best == start)
            {
                throw new TokenBudgetExceededException(
                    "The tokenizer reports one Unicode scalar above the token limit.");
            }

            parts.Add(segment[boundaries[start]..boundaries[best]]);
            start = best;
        }

        return parts;
    }

    private static bool ContainsOversizedSegment(string value)
    {
        int scalarCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                scalarCount = 0;
                continue;
            }

            scalarCount++;
            if (scalarCount > MaximumScalarsPerUnbrokenSegment)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class TokenBudgetExceededException(string message)
    : Exception(message);
