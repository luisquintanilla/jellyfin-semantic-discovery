using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

public sealed class HeadingAwareChunker(ITokenCounter tokenCounter, int maxTokens = 224)
{
    private readonly ITokenCounter _tokenCounter =
        tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));

    private readonly int _maxTokens = maxTokens > 0
        ? maxTokens
        : throw new ArgumentOutOfRangeException(nameof(maxTokens));

    public IReadOnlyList<ContentChunk> Chunk(
        Guid ownerId,
        string sourcePath,
        ContentSourceKind sourceKind,
        string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(markdown);
        if (sourceKind is not ContentSourceKind.Pdf and not ContentSourceKind.Epub)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceKind),
                sourceKind,
                "Heading-aware chunks are only supported for PDF and EPUB content.");
        }

        List<Section> sections = ParseSections(markdown);
        List<ContentChunk> chunks = [];

        foreach (Section section in sections)
        {
            (string heading, IReadOnlyList<string> bodyParts) =
                FitSection(section);
            string prefix = heading.Length == 0 ? string.Empty : $"{heading}\n";

            foreach (string part in bodyParts)
            {
                string projected = $"{prefix}{part}";
                chunks.Add(CreateChunk(
                    ownerId,
                    sourcePath,
                    sourceKind,
                    heading,
                    chunks.Count,
                    projected));
            }
        }

        return chunks;
    }

    private (string Heading, IReadOnlyList<string> BodyParts) FitSection(
        Section section)
    {
        IReadOnlyList<string> coarseParts = TokenBoundedTextSplitter.Split(
            section.Text,
            _tokenCounter,
            _maxTokens);
        List<int> headingBudgets = [];
        if (section.Heading.Length > 0)
        {
            for (int budget = Math.Max(1, _maxTokens - 1);
                 budget > 0;
                 budget /= 2)
            {
                headingBudgets.Add(budget);
                if (budget == 1)
                {
                    break;
                }
            }
        }
        headingBudgets.Add(0);

        foreach (int headingBudget in headingBudgets)
        {
            string heading;
            try
            {
                heading = headingBudget == 0
                    ? string.Empty
                    : TokenBoundedTextSplitter.Split(
                        section.Heading,
                        _tokenCounter,
                        headingBudget)[0];
            }
            catch (InvalidDataException)
            {
                continue;
            }

            string prefix = heading.Length == 0 ? string.Empty : $"{heading}\n";
            List<string> fitted = [];
            bool success = true;
            foreach (string coarsePart in coarseParts)
            {
                IReadOnlyList<string>? parts = FitBodyPart(
                    prefix,
                    coarsePart);
                if (parts is null)
                {
                    success = false;
                    break;
                }

                fitted.AddRange(parts);
            }

            if (success)
            {
                return (heading, fitted);
            }
        }

        throw new InvalidDataException(
            "The configured chunk budget cannot hold non-empty body text.");
    }

    private IReadOnlyList<string>? FitBodyPart(
        string prefix,
        string body)
    {
        if (_tokenCounter.CountTokens($"{prefix}{body}") <= _maxTokens)
        {
            return [body];
        }

        IReadOnlyList<string>? best = null;
        int low = 1;
        int high = _maxTokens;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            IReadOnlyList<string> candidate;
            try
            {
                candidate = TokenBoundedTextSplitter.Split(
                    body,
                    _tokenCounter,
                    middle);
            }
            catch (InvalidDataException)
            {
                low = middle + 1;
                continue;
            }

            if (candidate.All(part =>
                    _tokenCounter.CountTokens($"{prefix}{part}") <= _maxTokens))
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private static List<Section> ParseSections(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<Section> sections = [];
        string[] headings = new string[6];
        List<string> paragraph = [];

        void Flush()
        {
            string text = TextNormalization.NormalizeWhitespace(string.Join(' ', paragraph));
            if (text.Length > 0)
            {
                string heading = string.Join(
                    " > ",
                    headings.Where(static value => !string.IsNullOrEmpty(value)));
                sections.Add(new Section(heading, text));
            }

            paragraph.Clear();
        }

        foreach (string line in lines)
        {
            int headingLevel = GetHeadingLevel(line);
            if (headingLevel > 0)
            {
                Flush();
                headings[headingLevel - 1] =
                    TextNormalization.NormalizeWhitespace(line[headingLevel..]);
                Array.Clear(headings, headingLevel, headings.Length - headingLevel);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
            }
            else
            {
                paragraph.Add(line);
            }
        }

        Flush();
        return sections;
    }

    private static int GetHeadingLevel(string line)
    {
        int level = 0;
        while (level < line.Length && level < 6 && line[level] == '#')
        {
            level++;
        }

        return level > 0 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static ContentChunk CreateChunk(
        Guid ownerId,
        string sourcePath,
        ContentSourceKind sourceKind,
        string heading,
        int ordinal,
        string text)
    {
        string contentHash = ContentHash.Compute(text);
        return new ContentChunk
        {
            Id = ContentChunkIdentity.Compute(
                ownerId,
                sourcePath,
                sourceKind,
                ordinal,
                contentHash),
            OwnerId = ownerId,
            SourceKind = sourceKind,
            SourcePath = sourcePath,
            Ordinal = ordinal,
            Text = text,
            Heading = heading.Length == 0 ? null : heading,
            ContentHash = contentHash,
        };
    }

    private sealed record Section(string Heading, string Text);
}
