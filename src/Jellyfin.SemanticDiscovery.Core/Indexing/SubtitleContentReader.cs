using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.Core.Indexing;

public sealed class SubtitleContentReader : IContentReader
{
    private readonly SubtitleChunker _chunker;
    private readonly long _maximumDocumentBytes;

    public SubtitleContentReader(
        SubtitleChunker chunker,
        long maximumDocumentBytes = 2 * 1024 * 1024)
    {
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _maximumDocumentBytes = maximumDocumentBytes is > 0 and <= int.MaxValue - 1
            ? maximumDocumentBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
    }

    public bool Supports(ContentSourceKind kind) =>
        kind is ContentSourceKind.Subtitle or ContentSourceKind.Transcript;

    public async ValueTask<IReadOnlyList<ContentChunk>> ReadAsync(
        ContentSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Supports(source.Kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.Kind,
                "This reader accepts subtitle and transcript sources only.");
        }

        try
        {
            await using FileStream stream = new(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] bytes = await MarkItDownMcpReader.ReadBoundedDocumentAsync(
                stream,
                _maximumDocumentBytes,
                cancellationToken).ConfigureAwait(false);
            SemanticSourceIntegrity.ValidateIfPresent(
                source.Path,
                source.Kind,
                bytes);
            using MemoryStream buffer = new(bytes, writable: false);
            using StreamReader reader = new(buffer, leaveOpen: false);
            IReadOnlyList<SubtitleCue> cues =
                await SubtitleCueReader.ReadAsync(reader, cancellationToken)
                    .ConfigureAwait(false);
            return _chunker.Chunk(
                source.OwnerId,
                source.Path,
                source.Language,
                source.Kind,
                cues);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            throw new ContentIngestionException(
                "The subtitle or transcript source could not be ingested.",
                source.Path,
                isTransient: IsTransientReadFailure(exception),
                exception);
        }
    }

    internal static bool IsTransientReadFailure(Exception exception) =>
        exception is UnauthorizedAccessException ||
        exception is IOException and
            not FileNotFoundException and
            not DirectoryNotFoundException;
}
