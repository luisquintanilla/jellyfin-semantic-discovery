using CommunityToolkit.DocumentExtraction.VisionLanguageModel;
using Jellyfin.SemanticDiscovery.Core.Embedding;
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
    IReadOnlyList<IngestionDocument> Documents,
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
        if (document.Content is TextContent rootContent)
        {
            fragments.AddRange(ParseVisualText(
                page.PageNumber,
                rootContent.Text.Trim(),
                roleHeading: null,
                model));
        }

        foreach (ExtractionDocumentSection section in document.Sections)
        {
            AddElement(section, depth: 0);
        }

        if (fragments.Count == 0 &&
            document.RawRepresentation is ChatResponse response &&
            !string.IsNullOrWhiteSpace(response.Text))
        {
            fragments.AddRange(ParseVisualText(
                page.PageNumber,
                response.Text,
                roleHeading: null,
                model));
        }

        if (fragments.Count == 0)
        {
            throw new InvalidDataException(
                $"The vision model returned no extractable text for page " +
                $"{page.PageNumber}.");
        }

        return fragments;

        void AddElement(
            ExtractionDocumentElement element,
            int depth)
        {
            if (depth > 32)
            {
                throw new InvalidDataException(
                    "The visual extraction graph is too deeply nested.");
            }

            if (element is ExtractionDocumentTextElement textElement &&
                textElement.Content is TextContent textContent)
            {
                string text = textContent.Text.Trim();
                if (text.Length > 0)
                {
                    fragments.AddRange(ParseVisualText(
                        page.PageNumber,
                        text,
                        textElement.HeadingLevel.HasValue
                            ? text
                            : null,
                        model));
                }

                return;
            }

            if (element.Content is TextContent content &&
                !string.IsNullOrWhiteSpace(content.Text))
            {
                fragments.AddRange(ParseVisualText(
                    page.PageNumber,
                    content.Text.Trim(),
                    roleHeading: null,
                    model));
            }

            if (element is ExtractionDocumentSection nestedSection)
            {
                foreach (ExtractionDocumentElement child in
                         nestedSection.Elements)
                {
                    AddElement(child, depth + 1);
                }
            }
            else if (element is ExtractionDocumentTableElement table)
            {
                foreach (var cell in table.Cells)
                {
                    if (cell?.Content is { } cellContent)
                    {
                        AddElement(cellContent, depth + 1);
                    }
                }
            }
        }
    }

    private static List<ExtractedFragment> ParseVisualText(
        int pageNumber,
        string text,
        string? roleHeading,
        string model)
    {
        string[] provenance = [$"visual:{model}"];
        if (!string.IsNullOrWhiteSpace(roleHeading))
        {
            return
            [
                new(
                    pageNumber,
                    roleHeading,
                    roleHeading,
                    null,
                    null,
                    provenance),
            ];
        }

        List<ExtractedFragment> fragments = [];
        List<string> pending = [];
        bool foundHeading = false;
        foreach (string line in text
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            string trimmed = line.Trim();
            int markerLength = trimmed
                .TakeWhile(static character => character == '#')
                .Count();
            bool isHeading = markerLength is >= 1 and <= 6 &&
                trimmed.Length > markerLength &&
                char.IsWhiteSpace(trimmed[markerLength]);
            if (!isHeading)
            {
                pending.Add(line);
                continue;
            }

            CommitPending();
            string heading = trimmed[(markerLength + 1)..].Trim();
            if (heading.Length > 0)
            {
                foundHeading = true;
                fragments.Add(new ExtractedFragment(
                    pageNumber,
                    heading,
                    heading,
                    null,
                    null,
                    provenance));
            }
        }

        CommitPending();
        return foundHeading
            ? fragments
            :
            [
                new(
                    pageNumber,
                    text,
                    null,
                    null,
                    null,
                    provenance),
            ];

        void CommitPending()
        {
            string content = string.Join('\n', pending).Trim();
            pending.Clear();
            if (content.Length > 0)
            {
                fragments.Add(new ExtractedFragment(
                    pageNumber,
                    content,
                    null,
                    null,
                    null,
                    provenance));
            }
        }
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
                        Heading = existing.Heading ?? fragment.Heading,
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
    ExtractionOptions extractionOptions,
    ApplicationChunkTokenizer tokenizer) : IDocumentExtractionPipeline
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

        return await IngestionArtifactBuilder.BuildAsync(
            job,
            pages,
            tokenizer,
            cancellationToken).ConfigureAwait(false);
    }

    private static List<ExtractedFragment> CreateTextFragments(
        IReadOnlyList<PdfTextPage> pages,
        int pageNumber)
    {
        PdfTextPage? page = pages.SingleOrDefault(
            candidate => candidate.PageNumber == pageNumber);
        if (page is null || string.IsNullOrWhiteSpace(page.Text))
        {
            return [];
        }

        string[] lines = page.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return [];
        }

        string first = lines[0];
        bool heading = first.Length <= 160 &&
            first.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length <= 18 &&
            first.Any(char.IsLetter) &&
            !first.EndsWith('.') &&
            !first.EndsWith('?') &&
            !first.EndsWith('!');
        if (!heading)
        {
            return
            [
                new(
                    pageNumber,
                    page.Text,
                    null,
                    null,
                    null,
                    ["text-layer"]),
            ];
        }

        List<ExtractedFragment> fragments =
        [
            new(
                pageNumber,
                first,
                first,
                null,
                null,
                ["text-layer"]),
        ];
        string body = string.Join('\n', lines.Skip(1)).Trim();
        if (body.Length > 0)
        {
            fragments.Add(new ExtractedFragment(
                pageNumber,
                body,
                null,
                null,
                null,
                ["text-layer"]));
        }

        return fragments;
    }
}
