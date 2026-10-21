using System.Text;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Markdig;
using Markdig.Syntax;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal sealed class MarkdownIngestionDocumentAdapter
{
    private readonly MarkdownReader _reader = new();

    public async ValueTask<IngestionDocument> ReadAsync(
        ContentSourceDescriptor source,
        string markdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(markdown);
        if (source.Kind is not ContentSourceKind.Pdf and not ContentSourceKind.Epub)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.Kind,
                "Markdown documents are supported only for PDF and EPUB content.");
        }

        string identifier = IngestionDocumentIdentity.Create(source);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            IngestionDocument empty = new(identifier);
            empty.Sections.Add(new IngestionDocumentSection());
            return empty;
        }

        string normalizedMarkdown = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        await using MemoryStream stream = new(
            Encoding.UTF8.GetBytes(normalizedMarkdown),
            writable: false);
        IngestionDocument document = await _reader.ReadAsync(
            stream,
            identifier,
            "text/markdown",
            cancellationToken).ConfigureAwait(false);
        RestoreDroppedLeafBlocks(document, normalizedMarkdown);
        return document;
    }

    private static void RestoreDroppedLeafBlocks(
        IngestionDocument document,
        string markdown)
    {
        if (document.Sections.Count != 1)
        {
            throw new InvalidDataException(
                "The MEDI Markdown reader returned an unexpected section layout.");
        }

        IngestionDocumentSection root = document.Sections[0];
        IngestionDocumentElement[] mapped = root.Elements.ToArray();
        int mappedIndex = 0;
        root.Elements.Clear();

        MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        MarkdownDocument parsed = Markdown.Parse(markdown, pipeline);
        foreach (Block block in parsed)
        {
            if (block is ThematicBreakBlock or LinkReferenceDefinitionGroup)
            {
                continue;
            }

            if (block is LeafBlock leaf &&
                (leaf.Inline is null || leaf.Inline.FirstChild is null))
            {
                string rawMarkdown = markdown.Substring(
                    block.Span.Start,
                    block.Span.Length);
                if (!string.IsNullOrWhiteSpace(rawMarkdown))
                {
                    root.Elements.Add(
                        new IngestionDocumentParagraph(rawMarkdown)
                        {
                            Text = rawMarkdown,
                        });
                }

                continue;
            }

            if (mappedIndex >= mapped.Length)
            {
                throw new InvalidDataException(
                    "The MEDI Markdown reader omitted an unexpected block.");
            }

            root.Elements.Add(mapped[mappedIndex]);
            mappedIndex++;
        }

        if (mappedIndex != mapped.Length)
        {
            throw new InvalidDataException(
                "The MEDI Markdown reader returned an unexpected block mapping.");
        }
    }
}

internal static class TimedTextIngestionDocumentAdapter
{
    public static IngestionDocument Create(
        ContentSourceDescriptor source,
        IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cues);
        if (source.Kind is not ContentSourceKind.Subtitle and
            not ContentSourceKind.Transcript)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.Kind,
                "Timed-text documents are supported only for subtitles and transcripts.");
        }

        IngestionDocument document = new(IngestionDocumentIdentity.Create(source));
        IngestionDocumentSection section = new();
        document.Sections.Add(section);
        for (int ordinal = 0; ordinal < cues.Count; ordinal++)
        {
            SubtitleCue cue = cues[ordinal];
            IngestionDocumentParagraph paragraph = new(cue.Text)
            {
                Text = cue.Text,
            };
            paragraph.Metadata[IngestionMetadata.CueOrdinal] = ordinal;
            paragraph.Metadata[IngestionMetadata.StartTicks] = cue.StartTicks;
            paragraph.Metadata[IngestionMetadata.EndTicks] = cue.EndTicks;
            paragraph.Metadata[IngestionMetadata.Language] =
                source.Language ?? string.Empty;
            section.Elements.Add(paragraph);
        }

        return document;
    }
}

internal static class IngestionDocumentIdentity
{
    public static string Create(ContentSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Path);
        string material =
            $"{source.OwnerId:N}\n{(int)source.Kind}\n{source.Path}";
        return $"semantic-content:{ContentHash.Compute(material)}";
    }
}
