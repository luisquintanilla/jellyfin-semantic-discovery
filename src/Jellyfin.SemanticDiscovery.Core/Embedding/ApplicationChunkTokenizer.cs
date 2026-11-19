using System.Security.Cryptography;
using System.Text;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Microsoft.ML.Tokenizers;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public sealed class ApplicationChunkTokenizer : ITokenCounter
{
    public const string Model = "gpt-5";
    public const string NormalizationContract =
        "unicode-scalar-whitespace-v1";

    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForModel(Model);

    public static string IdentityHash => Convert.ToHexStringLower(
        SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"Microsoft.ML.Tokenizers/2.0.0|{Model}|" +
                NormalizationContract)));

    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _tokenizer.CountTokens(NormalizeWithSourceOffsets(text).Text);
    }

    public NormalizedText NormalizeWithSourceOffsets(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder normalized = new(text.Length);
        List<int> sourceOffsets = new(text.Length);
        bool pendingSpace = false;
        int pendingOffset = 0;
        Span<char> buffer = stackalloc char[2];

        foreach ((Rune rune, int sourceOffset) in EnumerateRunes(text))
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (normalized.Length > 0 && !pendingSpace)
                {
                    pendingSpace = true;
                    pendingOffset = sourceOffset;
                }

                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                sourceOffsets.Add(pendingOffset);
                pendingSpace = false;
            }

            int written = rune.EncodeToUtf16(buffer);
            normalized.Append(buffer[..written]);
            for (int index = 0; index < written; index++)
            {
                sourceOffsets.Add(sourceOffset);
            }
        }

        return new NormalizedText(
            normalized.ToString(),
            sourceOffsets.ToArray());
    }

    private static IEnumerable<(Rune Rune, int Offset)> EnumerateRunes(
        string text)
    {
        int offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            yield return (rune, offset);
            offset += rune.Utf16SequenceLength;
        }
    }
}

public sealed record NormalizedText(
    string Text,
    IReadOnlyList<int> SourceOffsets)
{
    public int GetSourceOffset(int normalizedOffset)
    {
        if (normalizedOffset == Text.Length)
        {
            return SourceOffsets.Count == 0
                ? 0
                : SourceOffsets[^1] + 1;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(normalizedOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            normalizedOffset,
            SourceOffsets.Count);

        return SourceOffsets[normalizedOffset];
    }
}
