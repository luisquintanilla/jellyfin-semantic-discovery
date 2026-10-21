using System.Runtime.CompilerServices;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal sealed class HeadingAwareChunker : IngestionChunker<string>
{
    private const int MaximumHeaderLevel = 10;
    private readonly MiniLmTokenBudget _tokenBudget;

    public HeadingAwareChunker(
        ITokenCounter tokenCounter,
        int maximumTokens = MiniLmTokenBudget.MaximumInputTokens)
    {
        _tokenBudget = new MiniLmTokenBudget(tokenCounter, maximumTokens);
    }

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IngestionDocument document,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Task.CompletedTask.ConfigureAwait(false);

        List<IngestionDocumentElement> elements = [];
        string?[] headings = new string?[MaximumHeaderLevel + 1];
        foreach (IngestionDocumentElement element in EnumerateTopLevelElements(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is not IngestionDocumentHeader header)
            {
                elements.Add(element);
                continue;
            }

            foreach (IngestionChunk<string> chunk in ChunkElements(
                         document,
                         headings,
                         elements,
                         cancellationToken))
            {
                yield return chunk;
            }

            elements.Clear();
            int level = Math.Clamp(
                header.Level.GetValueOrDefault(1),
                1,
                MaximumHeaderLevel);
            headings[level] = TextNormalization.NormalizeWhitespace(
                header.Text ?? header.GetMarkdown());
            headings.AsSpan(level + 1).Clear();
        }

        foreach (IngestionChunk<string> chunk in ChunkElements(
                     document,
                     headings,
                     elements,
                     cancellationToken))
        {
            yield return chunk;
        }
    }

    private List<IngestionChunk<string>> ChunkElements(
        IngestionDocument document,
        string?[] headings,
        List<IngestionDocumentElement> elements,
        CancellationToken cancellationToken)
    {
        if (elements.Count == 0)
        {
            return [];
        }

        foreach (string candidateContext in GetContextCandidates(headings))
        {
            try
            {
                return ChunkWithContext(
                    document,
                    candidateContext,
                    elements,
                    cancellationToken);
            }
            catch (TokenBudgetExceededException)
            {
            }
        }

        throw new InvalidDataException(
            "The configured model budget cannot hold non-empty document content.");
    }

    private List<IngestionChunk<string>> ChunkWithContext(
        IngestionDocument document,
        string context,
        List<IngestionDocumentElement> elements,
        CancellationToken cancellationToken)
    {
        List<IngestionChunk<string>> chunks = [];
        List<string> pending = [];
        foreach (IngestionDocumentElement element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string markdown = GetSemanticMarkdown(element);
            if (markdown.Length == 0)
            {
                continue;
            }

            string candidateBody = string.Join(
                "\n\n",
                pending.Append(markdown));
            if (pending.Count > 0 &&
                !_tokenBudget.Fits(
                    MiniLmTokenBudget.Combine(context, candidateBody)))
            {
                CommitPending();
            }

            if (_tokenBudget.Fits(
                    MiniLmTokenBudget.Combine(context, markdown)))
            {
                pending.Add(markdown);
                continue;
            }

            foreach (string part in _tokenBudget.SplitMarkdown(markdown, context))
            {
                CommitPending();
                string content = MiniLmTokenBudget.Combine(context, part);
                chunks.Add(CreateChunk(document, context, content));
            }
        }

        CommitPending();
        return chunks;

        void CommitPending()
        {
            if (pending.Count == 0)
            {
                return;
            }

            string body = string.Join("\n\n", pending);
            string content = MiniLmTokenBudget.Combine(context, body);
            chunks.Add(CreateChunk(document, context, content));
            pending.Clear();
        }
    }

    private IngestionChunk<string> CreateChunk(
        IngestionDocument document,
        string context,
        string content)
    {
        _tokenBudget.Validate(content);
        IngestionChunk<string> chunk = new(
            content,
            document,
            context.Length == 0 ? null : context);
        chunk.Metadata[IngestionMetadata.TokenCount] =
            _tokenBudget.CountTokens(content);
        return chunk;
    }

    private IEnumerable<string> GetContextCandidates(string?[] headings)
    {
        HashSet<string> yielded = new(StringComparer.Ordinal);
        string[] path = headings
            .Where(static heading => !string.IsNullOrWhiteSpace(heading))
            .Cast<string>()
            .ToArray();
        string context = string.Join(" > ", path);
        if (yielded.Add(context))
        {
            yield return context;
        }

        for (int start = 1; start < path.Length; start++)
        {
            string suffix = string.Join(" > ", path[start..]);
            if (yielded.Add(suffix))
            {
                yield return suffix;
            }
        }

        if (path.Length > 0)
        {
            string deepestHeading = path[^1];
            for (int budget = Math.Max(1, _tokenBudget.MaximumTokens / 2);
                 budget > 0;
                 budget /= 2)
            {
                IReadOnlyList<string> parts;
                try
                {
                    parts = _tokenBudget.SplitNormalized(
                        deepestHeading,
                        budget);
                }
                catch (TokenBudgetExceededException)
                {
                    continue;
                }

                if (parts.Count > 0 && yielded.Add(parts[0]))
                {
                    yield return parts[0];
                }

                if (budget == 1)
                {
                    break;
                }
            }
        }

        if (yielded.Add(string.Empty))
        {
            yield return string.Empty;
        }
    }

    private static IEnumerable<IngestionDocumentElement>
        EnumerateTopLevelElements(IngestionDocument document)
    {
        foreach (IngestionDocumentSection section in document.Sections)
        {
            foreach (IngestionDocumentElement element in section.Elements)
            {
                yield return element;
            }
        }
    }

    private static string GetSemanticMarkdown(
        IngestionDocumentElement element)
    {
        string? content = element switch
        {
            IngestionDocumentImage image =>
                image.AlternativeText ?? image.Text,
            _ => element.GetMarkdown(),
        };
        return content?
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim() ?? string.Empty;
    }
}
