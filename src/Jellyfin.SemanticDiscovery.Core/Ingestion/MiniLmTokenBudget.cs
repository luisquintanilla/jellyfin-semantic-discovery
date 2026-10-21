using System.Text;
using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal sealed class MiniLmTokenBudget
{
    internal const int MaximumInputTokens = 256;
    internal const int EmbeddingSpecialTokens = 2;
    internal const int MaximumScalarsPerUnbrokenSegment = 100;

    private readonly ITokenCounter _tokenCounter;

    public MiniLmTokenBudget(
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

    public IReadOnlyList<string> SplitMarkdown(
        string markdown,
        string context)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(context);

        string normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        if (Fits(Combine(context, normalized)))
        {
            return [normalized];
        }

        string[] lines = normalized.Split('\n');
        List<string> parts = [];
        List<string> pending = [];
        foreach (string line in lines)
        {
            string candidate = string.Join('\n', pending.Append(line));
            if (pending.Count > 0 && !Fits(Combine(context, candidate)))
            {
                parts.Add(string.Join('\n', pending).Trim());
                pending.Clear();
            }

            if (Fits(Combine(context, line)))
            {
                pending.Add(line);
                continue;
            }

            foreach (string fragment in SplitWithContext(line, context))
            {
                if (pending.Count > 0)
                {
                    parts.Add(string.Join('\n', pending).Trim());
                    pending.Clear();
                }

                parts.Add(fragment);
            }
        }

        if (pending.Count > 0)
        {
            parts.Add(string.Join('\n', pending).Trim());
        }

        return parts.Where(static part => part.Length > 0).ToArray();
    }

    public static string Combine(string context, string content) =>
        context.Length == 0 ? content : $"{context}\n{content}";

    private List<string> SplitWithContext(
        string text,
        string context)
    {
        List<int> boundaries = [0];
        int offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
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
                string candidate = text[
                    boundaries[start]..boundaries[middle]];
                if (Fits(Combine(context, candidate)))
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
                    "The model budget cannot hold one Unicode scalar with its context.");
            }

            parts.Add(text[boundaries[start]..boundaries[best]]);
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

    private static bool ContainsOversizedSegment(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment =>
                segment.EnumerateRunes()
                    .Take(MaximumScalarsPerUnbrokenSegment + 1)
                    .Count() > MaximumScalarsPerUnbrokenSegment);
}

internal sealed class TokenBudgetExceededException(string message)
    : Exception(message);
