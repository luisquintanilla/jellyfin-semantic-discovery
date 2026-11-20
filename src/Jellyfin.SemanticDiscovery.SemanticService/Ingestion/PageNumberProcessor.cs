using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.SemanticService.Ingestion;

/// <summary>
/// Tags every chunk with the page it starts on (so a search hit can say "page 4") and with
/// the hash of the PDF it came from (so an unchanged book isn't read twice).
/// </summary>
internal sealed class PageNumberProcessor(string sha256) : IngestionChunkProcessor<string>
{
    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IAsyncEnumerable<IngestionChunk<string>> chunks, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (IngestionChunk<string> chunk in chunks.WithCancellation(cancellationToken))
        {
            chunk.Metadata["page"] = PageOf(chunk) ?? 0;
            chunk.Metadata["sha256"] = sha256;
            yield return chunk;
        }
    }

    // The page of whichever element shows up first in the chunk. Elements can be split across
    // chunks, so look for either end of the element; tiny fragments ("4") would match anywhere.
    internal static int? PageOf(IngestionChunk<string> chunk) => chunk.Document.EnumerateContent()
        .Where(e => e.PageNumber is not null)
        .Select(e => (e.PageNumber, Position: PositionIn(chunk.Content, e.GetMarkdown().Trim())))
        .Where(e => e.Position >= 0)
        .OrderBy(e => e.Position)
        .FirstOrDefault().PageNumber;

    private static int PositionIn(string chunk, string text)
    {
        if (text.Length < 12)
        {
            return -1;
        }

        int length = Math.Min(text.Length, 48);
        int start = chunk.IndexOf(text[..length], StringComparison.Ordinal);
        int end = chunk.IndexOf(text[^length..], StringComparison.Ordinal);
        return start >= 0 ? start : end >= 0 ? Math.Max(0, end - (text.Length - length)) : -1;
    }
}
