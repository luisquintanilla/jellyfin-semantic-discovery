using System.Text;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal sealed class MarkdownIngestionDocumentAdapter
{
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

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
        cancellationToken.ThrowIfCancellationRequested();
        MarkdownDocument parsed = Markdown.Parse(
            normalizedMarkdown,
            MarkdownPipeline);
        cancellationToken.ThrowIfCancellationRequested();
        await using MemoryStream stream = new(
            Encoding.UTF8.GetBytes(normalizedMarkdown),
            writable: false);
        try
        {
            IngestionDocument document = await _reader.ReadAsync(
                stream,
                identifier,
                "text/markdown",
                cancellationToken).ConfigureAwait(false);
            if (TryRestoreDroppedLeafBlocks(
                    document,
                    parsed,
                    normalizedMarkdown) &&
                !RequiresImageSanitization(parsed, normalizedMarkdown) &&
                HasCompleteSemanticCoverage(
                    document,
                    parsed,
                    normalizedMarkdown))
            {
                return document;
            }
        }
        catch (NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CreateLosslessFallback(
            identifier,
            parsed,
            normalizedMarkdown);
    }

    private static bool TryRestoreDroppedLeafBlocks(
        IngestionDocument document,
        MarkdownDocument parsed,
        string markdown)
    {
        if (document.Sections.Count != 1)
        {
            return false;
        }

        IngestionDocumentSection root = document.Sections[0];
        IngestionDocumentElement[] mapped = root.Elements.ToArray();
        int mappedIndex = 0;
        root.Elements.Clear();

        foreach (Block block in parsed)
        {
            if (block is ThematicBreakBlock or LinkReferenceDefinitionGroup)
            {
                continue;
            }

            if (block is LeafBlock leaf &&
                (leaf.Inline is null || leaf.Inline.FirstChild is null))
            {
                string rawMarkdown = GetSanitizedBlockMarkdown(
                    block,
                    markdown);
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
                return false;
            }

            root.Elements.Add(mapped[mappedIndex]);
            mappedIndex++;
        }

        return mappedIndex == mapped.Length;
    }

    private static bool HasCompleteSemanticCoverage(
        IngestionDocument document,
        MarkdownDocument parsed,
        string markdown)
    {
        string sourceProjection = CreateSemanticProjection(
            parsed,
            markdown);
        StringBuilder mapped = new();
        foreach (IngestionDocumentSection section in document.Sections)
        {
            foreach (IngestionDocumentElement element in section.Elements)
            {
                if (element is IngestionDocumentImage image)
                {
                    AppendSemanticSegment(
                        mapped,
                        image.AlternativeText ?? image.Text);
                    continue;
                }

                string? elementMarkdown = element.GetMarkdown();
                if (string.IsNullOrWhiteSpace(elementMarkdown))
                {
                    continue;
                }

                MarkdownDocument elementDocument = Markdown.Parse(
                    elementMarkdown,
                    MarkdownPipeline);
                AppendSemanticBlocks(
                    mapped,
                    elementDocument,
                    elementMarkdown);
            }
        }

        return string.Equals(
            sourceProjection,
            NormalizeSemanticProjection(mapped),
            StringComparison.Ordinal);
    }

    private static IngestionDocument CreateLosslessFallback(
        string identifier,
        MarkdownDocument parsed,
        string markdown)
    {
        IngestionDocument document = new(identifier);
        IngestionDocumentSection root = new();
        document.Sections.Add(root);
        foreach (Block block in parsed)
        {
            if (block is ThematicBreakBlock or LinkReferenceDefinitionGroup)
            {
                continue;
            }

            string blockMarkdown = GetSanitizedBlockMarkdown(
                block,
                markdown);
            if (block is HeadingBlock heading)
            {
                string headingText = CreateSemanticProjection(
                    block,
                    markdown);
                if (!string.IsNullOrWhiteSpace(headingText))
                {
                    root.Elements.Add(
                        new IngestionDocumentHeader(blockMarkdown)
                        {
                            Level = heading.Level,
                            Text = headingText,
                        });
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(blockMarkdown))
            {
                blockMarkdown = CreateSemanticProjection(block, markdown);
            }

            if (!string.IsNullOrWhiteSpace(blockMarkdown))
            {
                root.Elements.Add(
                    new IngestionDocumentParagraph(blockMarkdown)
                    {
                        Text = blockMarkdown,
                    });
            }
        }

        if (root.Elements.Count == 0)
        {
            string semanticText = CreateSemanticProjection(parsed, markdown);
            if (!string.IsNullOrWhiteSpace(semanticText))
            {
                root.Elements.Add(
                    new IngestionDocumentParagraph(semanticText)
                    {
                        Text = semanticText,
                    });
            }
        }

        return document;
    }

    private static bool RequiresImageSanitization(
        MarkdownDocument parsed,
        string markdown)
    {
        foreach (Block block in parsed)
        {
            if (CollectImageReplacements(block, markdown).Count > 0 ||
                block is HtmlBlock &&
                ContainsHtmlImageTag(GetBlockMarkdown(block, markdown)))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetSanitizedBlockMarkdown(
        Block block,
        string markdown)
    {
        string rawMarkdown = GetBlockMarkdown(block, markdown);
        List<MarkdownReplacement> replacements =
            CollectImageReplacements(block, markdown);
        foreach (MarkdownReplacement replacement in replacements
                     .OrderByDescending(static replacement =>
                         replacement.Start))
        {
            int relativeStart = replacement.Start - block.Span.Start;
            if (relativeStart < 0 ||
                relativeStart + replacement.Length > rawMarkdown.Length)
            {
                return CreateSemanticProjection(block, markdown);
            }

            rawMarkdown = rawMarkdown.Remove(
                relativeStart,
                replacement.Length);
            rawMarkdown = rawMarkdown.Insert(
                relativeStart,
                replacement.Text);
        }

        return block is HtmlBlock
            ? SanitizeHtmlImageTags(rawMarkdown)
            : rawMarkdown;
    }

    private static string GetBlockMarkdown(
        Block block,
        string markdown)
    {
        if (block.Span.Start < 0 ||
            block.Span.Length <= 0 ||
            block.Span.Start + block.Span.Length > markdown.Length)
        {
            return string.Empty;
        }

        return markdown.Substring(block.Span.Start, block.Span.Length);
    }

    private static List<MarkdownReplacement> CollectImageReplacements(
        Block block,
        string markdown)
    {
        List<MarkdownReplacement> replacements = [];
        CollectImageReplacements(block, markdown, replacements);
        return replacements;
    }

    private static void CollectImageReplacements(
        Block block,
        string markdown,
        List<MarkdownReplacement> replacements)
    {
        if (block is LeafBlock leaf &&
            leaf.Inline?.FirstChild is not null)
        {
            CollectImageReplacements(
                leaf.Inline,
                markdown,
                replacements);
        }

        if (block is ContainerBlock container)
        {
            foreach (Block child in container)
            {
                CollectImageReplacements(
                    child,
                    markdown,
                    replacements);
            }
        }
    }

    private static void CollectImageReplacements(
        ContainerInline container,
        string markdown,
        List<MarkdownReplacement> replacements)
    {
        for (Inline? inline = container.FirstChild;
             inline is not null;
             inline = inline.NextSibling)
        {
            if (inline is LinkInline { IsImage: true } image)
            {
                AddReplacement(
                    replacements,
                    image,
                    CreateInlineSemanticProjection(image, markdown));
                continue;
            }

            if (inline is HtmlInline html &&
                ContainsHtmlImageTag(html.Tag))
            {
                AddReplacement(
                    replacements,
                    html,
                    SanitizeHtmlImageTags(html.Tag));
                continue;
            }

            if (inline is ContainerInline child)
            {
                CollectImageReplacements(
                    child,
                    markdown,
                    replacements);
            }
        }
    }

    private static void AddReplacement(
        List<MarkdownReplacement> replacements,
        MarkdownObject markdownObject,
        string text)
    {
        if (markdownObject.Span.Start >= 0 &&
            markdownObject.Span.Length > 0)
        {
            replacements.Add(
                new MarkdownReplacement(
                    markdownObject.Span.Start,
                    markdownObject.Span.Length,
                    text));
        }
    }

    private static string CreateSemanticProjection(
        MarkdownObject markdownObject,
        string markdown)
    {
        StringBuilder builder = new();
        switch (markdownObject)
        {
            case Block block:
                AppendSemanticBlock(builder, block, markdown);
                break;
            case ContainerInline inline:
                AppendSemanticInlines(builder, inline, markdown);
                break;
        }

        return NormalizeSemanticProjection(builder);
    }

    private static string CreateSemanticProjection(
        MarkdownDocument document,
        string markdown)
    {
        StringBuilder builder = new();
        AppendSemanticBlocks(builder, document, markdown);
        return NormalizeSemanticProjection(builder);
    }

    private static string CreateInlineSemanticProjection(
        ContainerInline container,
        string markdown)
    {
        StringBuilder builder = new();
        AppendSemanticInlines(builder, container, markdown);
        return NormalizeSemanticProjection(builder);
    }

    private static void AppendSemanticBlocks(
        StringBuilder builder,
        ContainerBlock container,
        string markdown)
    {
        foreach (Block block in container)
        {
            AppendSemanticBlock(builder, block, markdown);
        }
    }

    private static void AppendSemanticBlock(
        StringBuilder builder,
        Block block,
        string markdown)
    {
        if (block is ThematicBreakBlock or LinkReferenceDefinitionGroup)
        {
            return;
        }

        if (block is LeafBlock leaf)
        {
            if (leaf.Inline?.FirstChild is not null)
            {
                AppendSemanticInlines(builder, leaf.Inline, markdown);
            }
            else
            {
                AppendSemanticSegment(
                    builder,
                    block is HtmlBlock
                        ? SanitizeHtmlImageTags(
                            GetBlockMarkdown(block, markdown))
                        : GetBlockMarkdown(block, markdown));
            }

            AppendSemanticSegment(builder, "\n");
            return;
        }

        if (block is ContainerBlock container)
        {
            AppendSemanticBlocks(builder, container, markdown);
            return;
        }

        AppendSemanticSegment(
            builder,
            GetBlockMarkdown(block, markdown));
    }

    private static void AppendSemanticInlines(
        StringBuilder builder,
        ContainerInline container,
        string markdown)
    {
        for (Inline? inline = container.FirstChild;
             inline is not null;
             inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LinkInline image when image.IsImage:
                    AppendSemanticInlines(builder, image, markdown);
                    break;
                case LinkInline link:
                    AppendSemanticInlines(builder, link, markdown);
                    break;
                case HtmlInline html:
                    builder.Append(SanitizeHtmlImageTags(html.Tag));
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline child:
                    AppendSemanticInlines(builder, child, markdown);
                    break;
                default:
                    AppendSemanticSegment(
                        builder,
                        GetMarkdownObjectText(inline, markdown));
                    break;
            }
        }
    }

    private static string GetMarkdownObjectText(
        MarkdownObject markdownObject,
        string markdown)
    {
        if (markdownObject.Span.Start < 0 ||
            markdownObject.Span.Length <= 0 ||
            markdownObject.Span.Start + markdownObject.Span.Length >
                markdown.Length)
        {
            return string.Empty;
        }

        return markdown.Substring(
            markdownObject.Span.Start,
            markdownObject.Span.Length);
    }

    private static void AppendSemanticSegment(
        StringBuilder builder,
        string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            builder.Append(text);
        }
    }

    private static string NormalizeSemanticProjection(
        StringBuilder builder) =>
        TextNormalization.NormalizeWhitespace(builder.ToString());

    private static bool ContainsHtmlImageTag(string text) =>
        IndexOfHtmlImageTag(text, 0) >= 0;

    private static string SanitizeHtmlImageTags(string text)
    {
        int imageStart = IndexOfHtmlImageTag(text, 0);
        if (imageStart < 0)
        {
            return text;
        }

        StringBuilder sanitized = new(text.Length);
        int position = 0;
        while (imageStart >= 0)
        {
            sanitized.Append(text, position, imageStart - position);
            int tagEnd = FindHtmlTagEnd(text, imageStart);
            if (tagEnd < 0)
            {
                position = text.Length;
                break;
            }

            bool isSource = IsHtmlTagAt(text, imageStart, "source");
            if (!isSource)
            {
                sanitized.Append(
                    GetHtmlAttribute(
                        text.AsSpan(
                            imageStart,
                            tagEnd - imageStart + 1),
                        "alt"));
            }

            position = tagEnd + 1;
            imageStart = IndexOfHtmlImageTag(text, position);
        }

        sanitized.Append(text, position, text.Length - position);
        return sanitized.ToString();
    }

    private static int IndexOfHtmlImageTag(
        string text,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex);
             index < text.Length;
             index++)
        {
            if (text[index] != '<')
            {
                continue;
            }

            if (IsHtmlTagAt(text, index, "img") ||
                IsHtmlTagAt(text, index, "source"))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsHtmlTagAt(
        string text,
        int startIndex,
        string tagName)
    {
        int nameStart = startIndex + 1;
        if (nameStart + tagName.Length > text.Length ||
            !text.AsSpan(nameStart, tagName.Length)
                .Equals(tagName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int boundary = nameStart + tagName.Length;
        return boundary == text.Length ||
            char.IsWhiteSpace(text[boundary]) ||
            text[boundary] is '/' or '>';
    }

    private static int FindHtmlTagEnd(
        string text,
        int startIndex)
    {
        char quote = '\0';
        for (int index = startIndex + 1;
             index < text.Length;
             index++)
        {
            char current = text[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetHtmlAttribute(
        ReadOnlySpan<char> tag,
        string attributeName)
    {
        int position = 1;
        while (position < tag.Length)
        {
            while (position < tag.Length &&
                   (char.IsWhiteSpace(tag[position]) ||
                    tag[position] is '<' or '/' or '>'))
            {
                position++;
            }

            int nameStart = position;
            while (position < tag.Length &&
                   !char.IsWhiteSpace(tag[position]) &&
                   tag[position] is not '=' and not '>' and not '/')
            {
                position++;
            }

            ReadOnlySpan<char> name = tag[nameStart..position];
            while (position < tag.Length &&
                   char.IsWhiteSpace(tag[position]))
            {
                position++;
            }

            if (position >= tag.Length || tag[position] != '=')
            {
                continue;
            }

            position++;
            while (position < tag.Length &&
                   char.IsWhiteSpace(tag[position]))
            {
                position++;
            }

            char quote = position < tag.Length &&
                tag[position] is '"' or '\''
                    ? tag[position++]
                    : '\0';
            int valueStart = position;
            while (position < tag.Length &&
                   (quote == '\0'
                       ? !char.IsWhiteSpace(tag[position]) &&
                         tag[position] is not '>' and not '/'
                       : tag[position] != quote))
            {
                position++;
            }

            if (name.Equals(
                    attributeName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return System.Net.WebUtility.HtmlDecode(
                    tag[valueStart..position].ToString());
            }

            if (quote != '\0' && position < tag.Length)
            {
                position++;
            }
        }

        return string.Empty;
    }

    private readonly record struct MarkdownReplacement(
        int Start,
        int Length,
        string Text);
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
