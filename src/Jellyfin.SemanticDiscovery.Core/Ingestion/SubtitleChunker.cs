using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal sealed class SubtitleChunker : IngestionChunker<string>
{
    private readonly MiniLmTokenBudget _tokenBudget;

    public SubtitleChunker(
        ITokenCounter tokenCounter,
        int maximumTokens = MiniLmTokenBudget.MaximumInputTokens)
    {
        _tokenBudget = new MiniLmTokenBudget(tokenCounter, maximumTokens);
    }

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IngestionDocument document,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Task.CompletedTask.ConfigureAwait(false);

        List<CueFragment> pending = [];
        foreach (IngestionDocumentElement element in document.EnumerateContent())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is not IngestionDocumentParagraph paragraph)
            {
                throw new InvalidDataException(
                    "Timed-text documents may contain paragraph elements only.");
            }

            CueMetadata metadata = ReadCueMetadata(paragraph);
            string text = paragraph.Text ?? paragraph.GetMarkdown();
            foreach (string part in _tokenBudget.SplitNormalized(text))
            {
                CueFragment fragment = new(part, metadata);
                if (pending.Count > 0 &&
                    !_tokenBudget.Fits(Join(pending.Append(fragment))))
                {
                    yield return CreateChunk(document, pending);
                    pending.Clear();
                }

                pending.Add(fragment);
            }
        }

        if (pending.Count > 0)
        {
            yield return CreateChunk(document, pending);
        }
    }

    private IngestionChunk<string> CreateChunk(
        IngestionDocument document,
        IReadOnlyList<CueFragment> fragments)
    {
        string content = Join(fragments);
        _tokenBudget.Validate(content);
        string[] languages = fragments
            .Select(static fragment => fragment.Metadata.Language)
            .Where(static language => language.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (languages.Length > 1)
        {
            throw new InvalidDataException(
                "A timed-text document contains conflicting languages.");
        }

        IngestionChunk<string> chunk = new(content, document);
        chunk.Metadata[IngestionMetadata.TokenCount] =
            _tokenBudget.CountTokens(content);
        chunk.Metadata[IngestionMetadata.CueOrdinals] = fragments
            .Select(static fragment => fragment.Metadata.Ordinal)
            .Distinct()
            .ToArray();
        chunk.Metadata[IngestionMetadata.StartTicks] = fragments.Min(
            static fragment => fragment.Metadata.StartTicks);
        chunk.Metadata[IngestionMetadata.EndTicks] = fragments.Max(
            static fragment => fragment.Metadata.EndTicks);
        chunk.Metadata[IngestionMetadata.Language] =
            languages.FirstOrDefault() ?? string.Empty;
        return chunk;
    }

    private static CueMetadata ReadCueMetadata(
        IngestionDocumentParagraph paragraph) => new(
            GetRequiredInt32(paragraph, IngestionMetadata.CueOrdinal),
            GetRequiredInt64(paragraph, IngestionMetadata.StartTicks),
            GetRequiredInt64(paragraph, IngestionMetadata.EndTicks),
            GetRequiredString(paragraph, IngestionMetadata.Language));

    private static int GetRequiredInt32(
        IngestionDocumentElement element,
        string key)
    {
        if (element.Metadata.TryGetValue(key, out object? value) &&
            value is int result)
        {
            return result;
        }

        throw new InvalidDataException(
            $"Timed-text metadata '{key}' must be an integer.");
    }

    private static long GetRequiredInt64(
        IngestionDocumentElement element,
        string key)
    {
        if (element.Metadata.TryGetValue(key, out object? value))
        {
            return value switch
            {
                long result => result,
                int result => result,
                _ => throw new InvalidDataException(
                    $"Timed-text metadata '{key}' must be an integer."),
            };
        }

        throw new InvalidDataException(
            $"Timed-text metadata '{key}' is required.");
    }

    private static string GetRequiredString(
        IngestionDocumentElement element,
        string key)
    {
        if (element.Metadata.TryGetValue(key, out object? value) &&
            value is string result)
        {
            return result;
        }

        throw new InvalidDataException(
            $"Timed-text metadata '{key}' must be a string.");
    }

    private static string Join(IEnumerable<CueFragment> fragments) =>
        string.Join(' ', fragments.Select(static fragment => fragment.Text));

    private sealed record CueFragment(string Text, CueMetadata Metadata);

    private sealed record CueMetadata(
        int Ordinal,
        long StartTicks,
        long EndTicks,
        string Language);
}
