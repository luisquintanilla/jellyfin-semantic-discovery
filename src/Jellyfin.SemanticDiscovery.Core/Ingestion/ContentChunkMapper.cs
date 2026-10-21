using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal sealed class ContentChunkMapper
{
    private readonly MiniLmTokenBudget _tokenBudget;

    public ContentChunkMapper(
        ITokenCounter tokenCounter,
        int maximumTokens = MiniLmTokenBudget.MaximumInputTokens)
    {
        _tokenBudget = new MiniLmTokenBudget(tokenCounter, maximumTokens);
    }

    public async ValueTask<IReadOnlyList<ContentChunk>> MapAsync(
        ContentSourceDescriptor source,
        IAsyncEnumerable<IngestionChunk<string>> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunks);

        string expectedDocumentIdentifier = IngestionDocumentIdentity.Create(source);
        List<ContentChunk> mapped = [];
        await foreach (IngestionChunk<string> chunk in chunks
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!string.Equals(
                    chunk.Document.Identifier,
                    expectedDocumentIdentifier,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The ingestion chunk belongs to a different document.");
            }

            string text = NormalizeContent(chunk.Content);
            _tokenBudget.Validate(text);
            int recordedTokenCount = GetRequiredInt32(
                chunk.Metadata,
                IngestionMetadata.TokenCount);
            int exactTokenCount = _tokenBudget.CountTokens(text);
            if (recordedTokenCount != exactTokenCount)
            {
                throw new InvalidDataException(
                    "The ingestion chunk token count does not match the exact model count.");
            }

            string heading = TextNormalization.NormalizeWhitespace(chunk.Context);
            string contentHash = ContentHash.Compute(text);
            mapped.Add(new ContentChunk
            {
                Id = ContentChunkIdentity.Compute(
                    source.OwnerId,
                    source.Path,
                    source.Kind,
                    mapped.Count,
                    contentHash),
                OwnerId = source.OwnerId,
                SourceKind = source.Kind,
                SourcePath = source.Path,
                Ordinal = mapped.Count,
                Text = text,
                Language = GetOptionalString(
                    chunk.Metadata,
                    IngestionMetadata.Language),
                StartTicks = GetOptionalInt64(
                    chunk.Metadata,
                    IngestionMetadata.StartTicks),
                EndTicks = GetOptionalInt64(
                    chunk.Metadata,
                    IngestionMetadata.EndTicks),
                Heading = heading.Length == 0 ? null : heading,
                ContentHash = contentHash,
            });
        }

        return mapped;
    }

    private static string NormalizeContent(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static string? GetOptionalString(
        IDictionary<string, object> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (value is not string text)
        {
            throw new InvalidDataException(
                $"Ingestion metadata '{key}' must be a string.");
        }

        string normalized = TextNormalization.NormalizeWhitespace(text);
        return normalized.Length == 0 ? null : normalized;
    }

    private static long? GetOptionalInt64(
        IDictionary<string, object> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out object? value))
        {
            return null;
        }

        return value switch
        {
            long result => result,
            int result => result,
            _ => throw new InvalidDataException(
                $"Ingestion metadata '{key}' must be an integer."),
        };
    }

    private static int GetRequiredInt32(
        IDictionary<string, object> metadata,
        string key)
    {
        if (metadata.TryGetValue(key, out object? value) &&
            value is int result)
        {
            return result;
        }

        throw new InvalidDataException(
            $"Ingestion metadata '{key}' must be an integer.");
    }
}
