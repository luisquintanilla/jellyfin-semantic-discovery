using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace Jellyfin.SemanticDiscovery.SemanticService.Reading;

/// <summary>
/// Middleware that turns a PDF into one PNG per page (PDFtoImage, no external tools),
/// extracts each page with the inner client, and stitches the results back into one
/// document with page numbers.
/// </summary>
internal sealed class PdfPageRasterizer(DocumentExtractionClient inner) : DelegatingDocumentExtractionClient(inner)
{
    public override async Task<ExtractionDocument> ExtractAsync(
        Stream stream, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (mediaType != "application/pdf")
        {
            return await base.ExtractAsync(stream, identifier, mediaType, options, cancellationToken);
        }

        using MemoryStream pdf = new();
        await stream.CopyToAsync(pdf, cancellationToken);
        byte[] bytes = pdf.ToArray();
        int pageCount = PDFtoImage.Conversion.GetPageCount(bytes);

        List<ExtractionDocumentSection> sections = [];
        StringBuilder text = new();
        for (int i = 0; i < pageCount; i++)
        {
            using MemoryStream png = new();
            PDFtoImage.Conversion.SavePng(png, bytes, i, options: new(Dpi: 150));
            png.Position = 0;

            ExtractionDocument page = await base.ExtractAsync(png, $"{identifier}#page={i + 1}", "image/png", options, cancellationToken);
            ExtractionDocumentSourcePage source = new($"page-{i + 1}", i + 1, null, null, null);
            foreach (ExtractionDocumentSection section in page.Sections)
            {
                // The vision client sees one image, so it can't know the page number. Add it.
                if (section.Sources.FirstOrDefault()?.Page.PageNumber is null)
                {
                    section.Sources.Insert(0, new ExtractionDocumentSourceReference(source, [], null));
                }

                sections.Add(section);
            }

            if (page.Content is TextContent pageText)
            {
                text.AppendLine(pageText.Text).AppendLine();
            }
        }

        ExtractionDocument document = new(identifier, new TextContent(text.ToString().TrimEnd()));
        document.Sections.AddRange(sections);
        return document;
    }
}

/// <summary>
/// Middleware for small local models. If the model returns JSON that doesn't match the
/// schema (for example, a table with no cells), read the page again as plain markdown.
/// </summary>
internal sealed class PlainMarkdownRetry(DocumentExtractionClient structured, DocumentExtractionClient plain)
    : DelegatingDocumentExtractionClient(structured)
{
    public override async Task<ExtractionDocument> ExtractAsync(
        Stream stream, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        try
        {
            return await base.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
        }
        catch (InvalidDataException)
        {
            buffer.Position = 0;
            return await plain.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
        }
    }
}
