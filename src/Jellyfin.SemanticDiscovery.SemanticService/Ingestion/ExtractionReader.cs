using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace Jellyfin.SemanticDiscovery.SemanticService.Ingestion;

/// <summary>
/// The bridge between the two libraries. Any <see cref="DocumentExtractionClient"/> becomes
/// an <see cref="IngestionDocumentReader"/>: headings stay headings (so the chunker can split
/// on them), tables become markdown, figures keep their description, and every element keeps
/// its page number.
/// </summary>
internal sealed class ExtractionReader(DocumentExtractionClient client) : IngestionDocumentReader
{
    public override async Task<IngestionDocument> ReadAsync(
        Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        ExtractionDocument extracted = await client.ExtractAsync(source, identifier, mediaType, cancellationToken: cancellationToken);
        return Map(extracted, Path.GetFileNameWithoutExtension(identifier));
    }

    internal static IngestionDocument Map(ExtractionDocument extracted, string documentId)
    {
        IngestionDocument document = new(documentId);
        foreach (ExtractionDocumentSection section in extracted.Sections)
        {
            IngestionDocumentSection mapped = new();
            // Layout-aware providers nest sections, so walk down to the content elements.
            // Skip repeated page headers, footers, and page numbers; they only add noise.
            foreach (ExtractionDocumentElement element in Leaves(section).Where(e => !IsPageFurniture(e)))
            {
                if (Map(element) is { } item)
                {
                    item.PageNumber = PageOf(element) ?? PageOf(section);
                    mapped.Elements.Add(item);
                }
            }

            document.Sections.Add(mapped);
        }

        return document;
    }

    private static IngestionDocumentElement? Map(ExtractionDocumentElement element) => element switch
    {
        ExtractionDocumentTextElement { Content: TextContent t } text
            when text.HeadingLevel is not null || text.Role is "heading" or "title" or "sectionHeading"
            => new IngestionDocumentHeader(t.Text) { Level = text.HeadingLevel ?? 2 },
        ExtractionDocumentTableElement table => new IngestionDocumentParagraph(ToMarkdown(table) + "\n"),
        ExtractionDocumentImageElement image when (image.AlternativeText ?? image.Text) is { Length: > 0 } alt
            => new IngestionDocumentParagraph($"Figure: {alt}\n"),
        { Content: TextContent { Text.Length: > 0 } t } => new IngestionDocumentParagraph(t.Text + "\n"),
        _ => null,
    };

    private static IEnumerable<ExtractionDocumentElement> Leaves(ExtractionDocumentElement element) =>
        element is ExtractionDocumentSection section ? section.Elements.SelectMany(Leaves) : [element];

    private static bool IsPageFurniture(ExtractionDocumentElement element) =>
        element is ExtractionDocumentTextElement { Role: "pageHeader" or "pageFooter" or "pageNumber" };

    private static int? PageOf(ExtractionDocumentElement element) => element.Sources.FirstOrDefault()?.Page.PageNumber;

    private static string TextOf(ExtractionDocumentElement element) => element switch
    {
        ExtractionDocumentSection section => string.Join(" ", Leaves(section).Select(TextOf).Where(t => t.Length > 0)),
        { Content: TextContent t } => t.Text,
        _ => "",
    };

    private static string ToMarkdown(ExtractionDocumentTableElement table)
    {
        var cells = table.Cells;
        StringBuilder markdown = new();
        for (int row = 0; row < cells.RowCount; row++)
        {
            markdown.Append('|');
            for (int column = 0; column < cells.ColumnCount; column++)
            {
                var cell = cells[row, column];
                string text = cell?.Content is { } content && cell.RowIndex == row && cell.ColumnIndex == column
                    ? TextOf(content).ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal)
                    : "";
                markdown.Append(' ').Append(text).Append(" |");
            }

            markdown.AppendLine();
            if (row == 0)
            {
                markdown.Append('|').AppendJoin("", Enumerable.Repeat(" --- |", cells.ColumnCount)).AppendLine();
            }
        }

        return markdown.ToString();
    }
}
