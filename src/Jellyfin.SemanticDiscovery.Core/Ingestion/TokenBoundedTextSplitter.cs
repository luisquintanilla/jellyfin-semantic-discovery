using System.Text;
using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal static class TokenBoundedTextSplitter
{
    internal const int MaximumScalarsPerUnbrokenSegment = 100;

    public static IReadOnlyList<string> Split(
        string text,
        ITokenCounter tokenCounter,
        int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);

        string normalized = TextNormalization.NormalizeWhitespace(text);
        if (normalized.Length == 0)
        {
            return [];
        }

        if (tokenCounter.CountTokens(normalized) <= maxTokens &&
            !ContainsOversizedSegment(normalized))
        {
            return [normalized];
        }

        string[] words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
                    tokenCounter.CountTokens(candidate) <= maxTokens)
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
                    tokenCounter,
                    maxTokens));
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
                    string candidate = string.Join(' ', words[start..middle]);
                    if (!ContainsOversizedSegment(candidate) &&
                        tokenCounter.CountTokens(candidate) <= maxTokens)
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

    private static List<string> SplitOversizedSegment(
        string segment,
        ITokenCounter tokenCounter,
        int maxTokens)
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
                if (tokenCounter.CountTokens(candidate) <= maxTokens)
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
                throw new InvalidDataException(
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
