using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Extraction.Extraction;

public static class IngestionArtifactBuilder
{
    public static ExtractionArtifactBundle Build(
        ExtractionJob job,
        IReadOnlyList<ExtractedPage> pages,
        int maximumChunkCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumChunkCharacters,
            1);
        IngestionDocument document = new(job.ManifestItemId);
        List<IngestionChunk<string>> ingestionChunks = [];
        List<ExtractionArtifactChunk> artifactChunks = [];
        int sourceOffset = 0;

        foreach (ExtractedPage page in pages.OrderBy(
                     candidate => candidate.PageNumber))
        {
            IngestionDocumentSection section = new();
            document.Sections.Add(section);
            foreach (ExtractedFragment fragment in page.Fragments)
            {
                IngestionDocumentParagraph paragraph = new(fragment.Text)
                {
                    Text = fragment.Text,
                    PageNumber = page.PageNumber,
                };
                paragraph.Metadata["provenance"] =
                    fragment.Provenance.ToArray();
                section.Elements.Add(paragraph);

                foreach (string text in Split(
                             fragment.Text,
                             maximumChunkCharacters))
                {
                    int start = sourceOffset;
                    int end = checked(start + text.Length);
                    IngestionChunk<string> chunk = new(
                        text,
                        document,
                        fragment.Heading ?? string.Empty);
                    chunk.Metadata["ordinal"] = ingestionChunks.Count;
                    chunk.Metadata["pageNumber"] = page.PageNumber;
                    chunk.Metadata["sourceStart"] = start;
                    chunk.Metadata["sourceEnd"] = end;
                    chunk.Metadata["provenance"] =
                        fragment.Provenance.ToArray();
                    ingestionChunks.Add(chunk);
                    artifactChunks.Add(new ExtractionArtifactChunk(
                        artifactChunks.Count,
                        text,
                        fragment.Heading,
                        start,
                        end,
                        page.PageNumber,
                        fragment.Provenance));
                    sourceOffset = checked(end + 2);
                }
            }
        }

        ExtractionArtifact artifact = new(
            SchemaVersion: 1,
            job.ManifestItemId,
            job.SourceSha256,
            job.ExtractionProfile,
            job.Model,
            artifactChunks);
        return new ExtractionArtifactBundle(
            artifact,
            document,
            ingestionChunks);
    }

    private static IEnumerable<string> Split(
        string text,
        int maximumCharacters)
    {
        int offset = 0;
        while (offset < text.Length)
        {
            int length = Math.Min(maximumCharacters, text.Length - offset);
            if (offset + length < text.Length)
            {
                int breakAt = text.LastIndexOfAny(
                    ['\n', ' '],
                    offset + length - 1,
                    length);
                if (breakAt >= offset)
                {
                    length = breakAt - offset + 1;
                }
            }

            string chunk = text.Substring(offset, length).Trim();
            offset += length;
            if (chunk.Length > 0)
            {
                yield return chunk;
            }
        }
    }
}
