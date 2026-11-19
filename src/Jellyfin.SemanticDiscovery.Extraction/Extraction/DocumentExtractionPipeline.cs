using CommunityToolkit.DocumentExtraction.VisionLanguageModel;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Pdf;
using Jellyfin.SemanticDiscovery.Extraction.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;

namespace Jellyfin.SemanticDiscovery.Extraction.Extraction;

public sealed record ExtractedFragment(
    int PageNumber,
    string Text,
    string? Heading,
    long? SourceStart,
    long? SourceEnd,
    IReadOnlyList<string> Provenance);

public sealed record ExtractedPage(
    int PageNumber,
    IReadOnlyList<ExtractedFragment> Fragments);

public sealed record ExtractionArtifactBundle(
    ExtractionArtifact Artifact,
    IngestionDocument Document,
    IReadOnlyList<IngestionChunk<string>> Chunks);

public interface IVisionPageExtractor
{
    ValueTask<IReadOnlyList<ExtractedFragment>> ExtractAsync(
        RasterizedPdfPage page,
        string model,
        CancellationToken cancellationToken);
}

public sealed class VisionLanguagePageExtractor(IChatClient chatClient) :
    IVisionPageExtractor
{
    public async ValueTask<IReadOnlyList<ExtractedFragment>> ExtractAsync(
        RasterizedPdfPage page,
        string model,
        CancellationToken cancellationToken)
    {
        using VisionLanguageModelDocumentExtractionClient extractor = new(
            chatClient,
            new VisionLanguageModelDocumentExtractionClientOptions
            {
                Prompt =
                    "Transcribe this rendered PDF page faithfully as GitHub-flavored Markdown. Preserve headings, tables, and meaningful visual text. Do not invent content.",
                UseStructuredOutput = false,
            });
        await using MemoryStream stream = new(page.Content, writable: false);
        ExtractionDocument document = await extractor.ExtractAsync(
            stream,
            $"page-{page.PageNumber}.png",
            page.MediaType,
            new() { ExtractorName = model },
            cancellationToken).ConfigureAwait(false);
        List<ExtractedFragment> fragments = [];
        foreach (ExtractionDocumentTextElement element in
                 document.EnumerateContent()
                     .OfType<ExtractionDocumentTextElement>())
        {
            if (element.Content is not TextContent content)
            {
                continue;
            }

            string text = content.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            string? heading = string.Equals(
                element.Role,
                "heading",
                StringComparison.OrdinalIgnoreCase)
                ? text
                : null;
            fragments.Add(new ExtractedFragment(
                page.PageNumber,
                text,
                heading,
                null,
                null,
                [$"visual:{model}"]));
        }

        return fragments;
    }
}

public interface IProvenanceMerger
{
    ExtractedPage Merge(
        int pageNumber,
        IReadOnlyList<ExtractedFragment> textLayer,
        IReadOnlyList<ExtractedFragment> visual);
}

public sealed class ProvenanceMerger : IProvenanceMerger
{
    public ExtractedPage Merge(
        int pageNumber,
        IReadOnlyList<ExtractedFragment> textLayer,
        IReadOnlyList<ExtractedFragment> visual)
    {
        List<ExtractedFragment> merged = [];
        Dictionary<string, int> byText = new(StringComparer.Ordinal);
        Add(textLayer);
        Add(visual);
        return new ExtractedPage(pageNumber, merged);

        void Add(IReadOnlyList<ExtractedFragment> fragments)
        {
            foreach (ExtractedFragment fragment in fragments)
            {
                string text = NormalizeText(fragment.Text);
                if (text.Length == 0)
                {
                    continue;
                }

                string identity = NormalizeIdentity(text);
                if (byText.TryGetValue(identity, out int existingIndex))
                {
                    ExtractedFragment existing = merged[existingIndex];
                    merged[existingIndex] = existing with
                    {
                        Provenance = existing.Provenance
                            .Concat(fragment.Provenance)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                    };
                    continue;
                }

                byText.Add(identity, merged.Count);
                merged.Add(fragment with
                {
                    PageNumber = pageNumber,
                    Text = text,
                    Provenance = fragment.Provenance
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                });
            }
        }
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static string NormalizeIdentity(string text) =>
        string.Join(
            ' ',
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
}

public interface IDocumentExtractionPipeline
{
    ValueTask<ExtractionArtifactBundle> ExtractAsync(
        ExtractionJob job,
        ValidatedExtractionSource source,
        CancellationToken cancellationToken);
}

public sealed class DocumentExtractionPipeline(
    IPdfTextLayerExtractor textLayerExtractor,
    IPdfRasterizer rasterizer,
    IVisionPageExtractor visionExtractor,
    IProvenanceMerger provenanceMerger,
    ExtractionOptions extractionOptions) : IDocumentExtractionPipeline
{
    public async ValueTask<ExtractionArtifactBundle> ExtractAsync(
        ExtractionJob job,
        ValidatedExtractionSource source,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PdfTextPage> textPages =
            job.ExtractionProfile is ExtractionProfile.Text or
                ExtractionProfile.Hybrid
                ? await textLayerExtractor.ExtractAsync(
                    source.SourcePath,
                    source.PageCount,
                    cancellationToken).ConfigureAwait(false)
                : [];
        string workspace = Path.Combine(
            Path.GetFullPath(extractionOptions.StateRoot),
            "work",
            job.JobId);
        List<ExtractedPage> pages = new(source.PageCount);
        try
        {
            for (int pageNumber = 1;
                 pageNumber <= source.PageCount;
                 pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<ExtractedFragment> text = CreateTextFragments(
                    textPages,
                    pageNumber);
                IReadOnlyList<ExtractedFragment> visual = [];
                if (job.ExtractionProfile is ExtractionProfile.Vision or
                    ExtractionProfile.Hybrid)
                {
                    RasterizedPdfPage image = await rasterizer.RasterizeAsync(
                        source.SourcePath,
                        pageNumber,
                        extractionOptions.RasterDpi,
                        workspace,
                        cancellationToken).ConfigureAwait(false);
                    visual = await visionExtractor.ExtractAsync(
                        image,
                        job.Model,
                        cancellationToken).ConfigureAwait(false);
                }

                pages.Add(provenanceMerger.Merge(pageNumber, text, visual));
            }
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }

        return IngestionArtifactBuilder.Build(
            job,
            pages,
            extractionOptions.MaximumChunkCharacters);
    }

    private static IReadOnlyList<ExtractedFragment> CreateTextFragments(
        IReadOnlyList<PdfTextPage> pages,
        int pageNumber)
    {
        PdfTextPage? page = pages.SingleOrDefault(
            candidate => candidate.PageNumber == pageNumber);
        return page is null || string.IsNullOrWhiteSpace(page.Text)
            ? []
            :
            [
                new ExtractedFragment(
                    pageNumber,
                    page.Text,
                    null,
                    null,
                    null,
                    ["text-layer"]),
            ];
    }
}
