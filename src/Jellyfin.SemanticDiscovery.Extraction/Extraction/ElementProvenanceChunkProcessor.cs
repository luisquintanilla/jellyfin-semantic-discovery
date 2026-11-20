using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Extraction.Extraction;

/// <summary>
/// Stamps page, provenance, and heading metadata onto chunks produced by a
/// built-in chunker. The built-in chunkers emit content without recording
/// which document elements each chunk came from, so this processor walks the
/// chunks in order and attributes their non-whitespace characters back to the
/// elements that produced them.
/// </summary>
internal sealed class ElementProvenanceChunkProcessor : IngestionChunkProcessor<string>
{
    public const string PageNumberKey = "pageNumber";
    public const string ProvenanceKey = "provenance";
    public const string HeadingKey = "heading";

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IAsyncEnumerable<IngestionChunk<string>> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        IngestionDocument? document = null;
        List<IngestionDocumentElement> owners = [];
        int cursor = 0;
        await foreach (IngestionChunk<string> chunk in chunks
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!ReferenceEquals(chunk.Document, document))
            {
                document = chunk.Document;
                owners = MapCharactersToElements(document);
                cursor = 0;
            }

            string body = chunk.Content;
            if (!string.IsNullOrEmpty(chunk.Context))
            {
                if (!body.StartsWith(chunk.Context, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Chunk content does not start with its header context.");
                }

                body = body[chunk.Context.Length..];
            }

            int characters = body.Count(static c => !char.IsWhiteSpace(c));
            if (characters == 0 || cursor + characters > owners.Count)
            {
                throw new InvalidDataException(
                    $"Chunk content could not be attributed to elements of '{document.Identifier}'.");
            }

            IngestionDocumentElement[] sources = owners
                .GetRange(cursor, characters)
                .Distinct()
                .ToArray();
            cursor += characters;

            int[] pageNumbers = sources
                .Where(static element => element.PageNumber.HasValue)
                .Select(static element => element.PageNumber!.Value)
                .ToArray();
            if (pageNumbers.Length > 0)
            {
                chunk.Metadata[PageNumberKey] = pageNumbers.Min();
            }

            chunk.Metadata[ProvenanceKey] = sources
                .SelectMany(static element => GetStrings(element, ProvenanceKey))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (sources[0].Metadata.TryGetValue(HeadingKey, out object? heading) &&
                heading is string text)
            {
                chunk.Metadata[HeadingKey] = text;
            }

            yield return chunk;
        }
    }

    private static List<IngestionDocumentElement> MapCharactersToElements(
        IngestionDocument document)
    {
        List<IngestionDocumentElement> owners = [];
        foreach (IngestionDocumentElement element in document.EnumerateContent())
        {
            if (element is IngestionDocumentHeader)
            {
                continue;
            }

            // Mirrors the semantic content the built-in chunkers embed.
            string? content = element is IngestionDocumentImage image
                ? image.AlternativeText ?? image.Text
                : element.GetMarkdown();
            foreach (char character in content ?? string.Empty)
            {
                if (!char.IsWhiteSpace(character))
                {
                    owners.Add(element);
                }
            }
        }

        return owners;
    }

    private static IEnumerable<string> GetStrings(
        IngestionDocumentElement element,
        string key) =>
        element.Metadata.TryGetValue(key, out object? value)
            ? value switch
            {
                string text => [text],
                IEnumerable<string> values => values,
                _ => [],
            }
            : [];
}
