using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;

namespace Jellyfin.SemanticDiscovery.Extraction.Extraction;

public static class IngestionArtifactBuilder
{
    private static readonly Tokenizer ChunkTokenizer =
        TiktokenTokenizer.CreateForModel(ApplicationChunkTokenizer.Model);

    public static async ValueTask<ExtractionArtifactBundle> BuildAsync(
        ExtractionJob job,
        IReadOnlyList<ExtractedPage> pages,
        ApplicationChunkTokenizer tokenizer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(tokenizer);

        IReadOnlyList<IngestionDocument> documents = CreatePageDocuments(job, pages);
        HeaderChunker chunker = new(new IngestionChunkerOptions(ChunkTokenizer)
        {
            MaxTokensPerChunk = SemanticConstants.MaximumChunkTokens,
        });
        ElementProvenanceChunkProcessor provenance = new();
        List<IngestionChunk<string>> ingestionChunks = [];
        List<ExtractionArtifactChunk> artifactChunks = [];
        int sourceOffset = 0;
        await foreach (IngestionChunk<string> chunk in provenance
                           .ProcessAsync(
                               ChunkAllAsync(chunker, documents, cancellationToken),
                               cancellationToken)
                           .ConfigureAwait(false))
        {
            // The built-in chunker joins elements with Environment.NewLine;
            // normalize so artifacts are identical on every OS.
            string text = chunk.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            int tokenCount = tokenizer.CountTokens(text);
            if (tokenCount > SemanticConstants.MaximumChunkTokens)
            {
                throw new InvalidDataException(
                    $"Chunk {artifactChunks.Count} contains {tokenCount} " +
                    $"tokens, above the {SemanticConstants.MaximumChunkTokens}-token limit.");
            }

            chunk.Metadata[IngestionMetadata.TokenCount] = tokenCount;
            int start = sourceOffset;
            int end = checked(start + text.Length);
            ingestionChunks.Add(chunk);
            artifactChunks.Add(new ExtractionArtifactChunk(
                artifactChunks.Count,
                text,
                chunk.Metadata.TryGetValue(
                    ElementProvenanceChunkProcessor.HeadingKey,
                    out object? heading)
                    ? heading as string
                    : null,
                start,
                end,
                chunk.Metadata.TryGetValue(
                    ElementProvenanceChunkProcessor.PageNumberKey,
                    out object? page)
                    ? page as int?
                    : null,
                tokenCount,
                (string[])chunk.Metadata[ElementProvenanceChunkProcessor.ProvenanceKey]));
            sourceOffset = checked(end + 2);
        }

        ExtractionArtifact artifact = new(
            SchemaVersion: 1,
            job.ManifestItemId,
            job.SourceSha256,
            job.ExtractionProfile,
            job.Model,
            ApplicationChunkTokenizer.IdentityHash,
            SemanticConstants.DocumentChunkingIdentity,
            artifactChunks);
        return new ExtractionArtifactBundle(
            artifact,
            documents,
            ingestionChunks);
    }

    private static async IAsyncEnumerable<IngestionChunk<string>> ChunkAllAsync(
        IngestionChunker<string> chunker,
        IReadOnlyList<IngestionDocument> documents,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        foreach (IngestionDocument document in documents)
        {
            await foreach (IngestionChunk<string> chunk in chunker
                               .ProcessAsync(document, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    // One document per page keeps every chunk on a single page. The heading in
    // effect at the end of a page is repeated at the start of the next page so
    // continuation text keeps its section context.
    private static List<IngestionDocument> CreatePageDocuments(
        ExtractionJob job,
        IReadOnlyList<ExtractedPage> pages)
    {
        List<IngestionDocument> documents = [];
        string? heading = null;
        foreach (ExtractedPage page in pages.OrderBy(
                     static candidate => candidate.PageNumber))
        {
            IngestionDocument document = new(
                $"{job.ManifestItemId}#page={page.PageNumber}");
            IngestionDocumentSection section = new() { PageNumber = page.PageNumber };
            document.Sections.Add(section);
            if (heading is not null &&
                (page.Fragments.Count == 0 ||
                 string.IsNullOrWhiteSpace(page.Fragments[0].Heading)))
            {
                section.Elements.Add(CreateHeader(heading, page.PageNumber));
            }

            foreach (ExtractedFragment fragment in page.Fragments)
            {
                if (!string.IsNullOrWhiteSpace(fragment.Heading))
                {
                    if (!string.Equals(fragment.Heading, heading, StringComparison.Ordinal) ||
                        section.Elements.Count == 0)
                    {
                        section.Elements.Add(CreateHeader(fragment.Heading, page.PageNumber));
                    }

                    heading = fragment.Heading;
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
                paragraph.Metadata[ElementProvenanceChunkProcessor.ProvenanceKey] =
                    fragment.Provenance.ToArray();
                if (heading is not null)
                {
                    paragraph.Metadata[ElementProvenanceChunkProcessor.HeadingKey] = heading;
                }

                section.Elements.Add(paragraph);
            }

            documents.Add(document);
        }

        return documents;
    }

    private static IngestionDocumentHeader CreateHeader(string heading, int pageNumber) =>
        new($"# {heading}")
        {
            Level = 1,
            Text = heading,
            PageNumber = pageNumber,
        };
}