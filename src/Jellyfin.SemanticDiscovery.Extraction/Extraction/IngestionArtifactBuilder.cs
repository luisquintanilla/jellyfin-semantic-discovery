using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Extraction.Extraction;

public static class IngestionArtifactBuilder
{
    public static async ValueTask<ExtractionArtifactBundle> BuildAsync(
        ExtractionJob job,
        IReadOnlyList<ExtractedPage> pages,
        ApplicationChunkTokenizer tokenizer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(tokenizer);

        IngestionDocument document = CreateDocument(job, pages);
        HeadingAwareChunker chunker = new(tokenizer);
        List<IngestionChunk<string>> ingestionChunks = [];
        List<ExtractionArtifactChunk> artifactChunks = [];
        int sourceOffset = 0;
        await foreach (IngestionChunk<string> chunk in chunker
                           .ProcessAsync(document, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            int tokenCount = GetRequiredInt(
                chunk.Metadata,
                IngestionMetadata.TokenCount);
            if (tokenCount > HeadingAwareChunker.MaximumTokens)
            {
                throw new InvalidDataException(
                    $"Chunk {artifactChunks.Count} contains {tokenCount} " +
                    $"tokens, above the {HeadingAwareChunker.MaximumTokens}-token limit.");
            }

            string text = chunk.Content;
            int start = sourceOffset;
            int end = checked(start + text.Length);
            int? pageNumber = GetPageNumbers(chunk.Metadata)
                .Order()
                .Cast<int?>()
                .FirstOrDefault();
            string[] provenance = GetStrings(
                    chunk.Metadata,
                    "provenance")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            ingestionChunks.Add(chunk);
            artifactChunks.Add(new ExtractionArtifactChunk(
                artifactChunks.Count,
                text,
                string.IsNullOrWhiteSpace(chunk.Context)
                    ? null
                    : chunk.Context,
                start,
                end,
                pageNumber,
                tokenCount,
                provenance));
            sourceOffset = checked(end + 2);
        }

        ExtractionArtifact artifact = new(
            SchemaVersion: 1,
            job.ManifestItemId,
            job.SourceSha256,
            job.ExtractionProfile,
            job.Model,
            ApplicationChunkTokenizer.IdentityHash,
            HeadingAwareChunker.Identity,
            artifactChunks);
        return new ExtractionArtifactBundle(
            artifact,
            document,
            ingestionChunks);
    }

    private static IngestionDocument CreateDocument(
        ExtractionJob job,
        IReadOnlyList<ExtractedPage> pages)
    {
        IngestionDocument document = new(job.ManifestItemId);
        foreach (ExtractedPage page in pages.OrderBy(
                     static candidate => candidate.PageNumber))
        {
            IngestionDocumentSection section = new();
            document.Sections.Add(section);
            foreach (ExtractedFragment fragment in page.Fragments)
            {
                if (!string.IsNullOrWhiteSpace(fragment.Heading))
                {
                    IngestionDocumentHeader header = new(
                        $"# {fragment.Heading}")
                    {
                        Level = 1,
                        Text = fragment.Heading,
                        PageNumber = page.PageNumber,
                    };
                    header.Metadata["provenance"] =
                        fragment.Provenance.ToArray();
                    section.Elements.Add(header);
                    if (string.Equals(
                            fragment.Text,
                            fragment.Heading,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                IngestionDocumentParagraph paragraph = new(fragment.Text)
                {
                    Text = fragment.Text,
                    PageNumber = page.PageNumber,
                };
                paragraph.Metadata["provenance"] =
                    fragment.Provenance.ToArray();
                section.Elements.Add(paragraph);
            }
        }

        return document;
    }

    private static int GetRequiredInt(
        IDictionary<string, object> metadata,
        string key) =>
        metadata.TryGetValue(key, out object? value) &&
        value is int result
            ? result
            : throw new InvalidDataException(
                $"Chunk metadata '{key}' is missing.");

    private static IEnumerable<int> GetPageNumbers(
        IDictionary<string, object> metadata) =>
        metadata.TryGetValue("pageNumbers", out object? value)
            ? value switch
            {
                int page => [page],
                IEnumerable<int> pages => pages,
                _ => [],
            }
            : [];

    private static IEnumerable<string> GetStrings(
        IDictionary<string, object> metadata,
        string key) =>
        metadata.TryGetValue(key, out object? value)
            ? value switch
            {
                string text => [text],
                IEnumerable<string> values => values,
                _ => [],
            }
            : [];
}
