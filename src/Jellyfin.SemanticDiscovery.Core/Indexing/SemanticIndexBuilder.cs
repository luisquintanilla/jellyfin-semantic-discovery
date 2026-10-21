using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.VectorData;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Core.Indexing;

public sealed class SemanticIndexBuilder(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ITokenCounter tokenCounter,
    IIndexSource source,
    IEnumerable<IContentReader> contentReaders,
    IIndexSnapshotStore snapshotStore,
    InMemorySemanticVectorIndex vectorIndex,
    IndexIdentity identity,
    int embeddingBatchSize = 8)
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator =
        embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));

    private readonly ITokenCounter _tokenCounter =
        tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));

    private readonly IIndexSource _source =
        source ?? throw new ArgumentNullException(nameof(source));

    private readonly IContentReader[] _contentReaders =
        contentReaders?.ToArray() ?? throw new ArgumentNullException(nameof(contentReaders));

    private readonly IIndexSnapshotStore _snapshotStore =
        snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));

    private readonly InMemorySemanticVectorIndex _vectorIndex =
        vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));

    private readonly IndexIdentity _identity =
        identity ?? throw new ArgumentNullException(nameof(identity));

    private readonly int _embeddingBatchSize = embeddingBatchSize > 0
        ? embeddingBatchSize
        : throw new ArgumentOutOfRangeException(nameof(embeddingBatchSize));

    public string? LastSnapshotLoadError { get; private set; }

    public async ValueTask<bool> TryLoadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        IndexSnapshot? snapshot =
            await LoadReusableSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null ||
            !string.Equals(
                snapshot.Identity.CompositeHash,
                _identity.CompositeHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        InMemorySemanticVectorIndex.PreparedState prepared =
            await _vectorIndex.PrepareAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        _vectorIndex.Publish(prepared);
        return true;
    }

    public async ValueTask<IndexBuildResult> RebuildAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IndexSnapshot? existing =
            await LoadReusableSnapshotAsync(cancellationToken).ConfigureAwait(false);
        bool canReuse = existing is not null &&
            string.Equals(
                existing.Identity.CompositeHash,
                _identity.CompositeHash,
                StringComparison.Ordinal);

        Dictionary<Guid, PersistedMetadataVector> existingMetadata = canReuse
            ? existing!.Metadata.ToDictionary(static item => item.Record.Id)
            : [];
        Dictionary<string, PersistedContentVector> existingContent = canReuse
            ? existing!.Content.ToDictionary(static item => item.Chunk.Id)
            : [];

        List<PendingMetadata> pendingMetadata = [];
        List<PendingContent> pendingContent = [];
        List<PersistedMetadataVector> metadata = [];
        List<PersistedContentVector> content = [];
        List<ContentIngestionError> errors = [];
        List<IndexSourceItem> sourceItems = [];

        await foreach (IndexSourceItem item in _source
                           .GetItemsAsync(cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            sourceItems.Add(item);
        }

        for (int itemIndex = 0; itemIndex < sourceItems.Count; itemIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IndexSourceItem item = sourceItems[itemIndex];
            string canonicalProjection =
                MetadataProjectionIdentity.CreateCanonicalText(item.Metadata);
            string projection = BoundMetadataProjection(canonicalProjection);
            string projectionHash = ContentHash.Compute(canonicalProjection);

            if (existingMetadata.TryGetValue(item.Metadata.Id, out PersistedMetadataVector? prior) &&
                string.Equals(
                    prior.ProjectionHash,
                    projectionHash,
                    StringComparison.Ordinal))
            {
                metadata.Add(prior with { Record = item.Metadata });
            }
            else
            {
                pendingMetadata.Add(new PendingMetadata(
                    item.Metadata,
                    projection,
                    projectionHash));
            }

            foreach (ContentSourceDescriptor contentSource in item.ContentSources)
            {
                if (contentSource.OwnerId != item.Metadata.Id)
                {
                    throw new InvalidDataException(
                        $"Content source '{contentSource.Path}' is assigned to a different metadata owner.");
                }

                IContentReader? reader = _contentReaders.FirstOrDefault(
                    candidate => candidate.Supports(contentSource.Kind));
                if (reader is null)
                {
                    throw new InvalidOperationException(
                        $"No content reader supports {contentSource.Kind}.");
                }

                try
                {
                    IReadOnlyList<ContentChunk> chunks =
                        await reader.ReadAsync(contentSource, cancellationToken)
                            .ConfigureAwait(false);
                    foreach (ContentChunk chunk in chunks)
                    {
                        if (chunk.OwnerId != contentSource.OwnerId ||
                            chunk.SourceKind != contentSource.Kind ||
                            !string.Equals(
                                chunk.SourcePath,
                                contentSource.Path,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"Content reader returned a chunk outside source '{contentSource.Path}'.");
                        }

                        if (existingContent.TryGetValue(
                                chunk.Id,
                                out PersistedContentVector? priorChunk) &&
                            string.Equals(
                                priorChunk.Chunk.ContentHash,
                                chunk.ContentHash,
                                StringComparison.Ordinal))
                        {
                            content.Add(priorChunk with { Chunk = chunk });
                        }
                        else
                        {
                            pendingContent.Add(new PendingContent(chunk));
                        }
                    }
                }
                catch (ContentIngestionException exception)
                {
                    errors.Add(new ContentIngestionError(
                        exception.SourcePath,
                        exception.Message,
                        exception.IsTransient));
                    if (exception.IsTransient)
                    {
                        content.AddRange(existingContent.Values.Where(item =>
                            item.Chunk.OwnerId == contentSource.OwnerId &&
                            item.Chunk.SourceKind == contentSource.Kind &&
                            string.Equals(
                                item.Chunk.SourcePath,
                                contentSource.Path,
                                StringComparison.Ordinal)));
                    }
                }
            }

            progress?.Report(sourceItems.Count == 0
                ? 0
                : (itemIndex + 1D) / sourceItems.Count * 50D);
        }

        int embeddedMetadata = await EmbedMetadataAsync(
            pendingMetadata,
            metadata,
            cancellationToken).ConfigureAwait(false);
        int embeddedContent = await EmbedContentAsync(
            pendingContent,
            content,
            cancellationToken).ConfigureAwait(false);

        PersistedMetadataVector[] metadataSnapshot = metadata
            .OrderBy(static item => item.Record.Id)
            .ToArray();
        PersistedContentVector[] contentSnapshot = content
            .OrderBy(static item => item.Chunk.OwnerId)
            .ThenBy(static item => item.Chunk.SourcePath, StringComparer.Ordinal)
            .ThenBy(static item => item.Chunk.Ordinal)
            .ToArray();
        IndexSnapshot snapshot = new()
        {
            Identity = _identity,
            CreatedUtc = DateTimeOffset.UtcNow,
            Metadata = metadataSnapshot,
            Content = contentSnapshot,
            VectorPayloadHash = IndexSnapshotVectorHash.Compute(
                metadataSnapshot,
                contentSnapshot),
        };

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(99D);
        InMemorySemanticVectorIndex.PreparedState prepared =
            await _vectorIndex.PrepareAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        await _snapshotStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        _vectorIndex.Publish(prepared);

        return new IndexBuildResult(
            snapshot.Metadata.Length,
            snapshot.Content.Length,
            embeddedMetadata,
            embeddedContent,
            errors);
    }

    private string BoundMetadataProjection(string projection)
    {
        IReadOnlyList<string> parts =
            TokenBoundedTextSplitter.Split(projection, _tokenCounter, maxTokens: 256);
        return parts.Count == 0 ? string.Empty : parts[0];
    }

    private async ValueTask<IndexSnapshot?> LoadReusableSnapshotAsync(
        CancellationToken cancellationToken)
    {
        LastSnapshotLoadError = null;
        try
        {
            IndexSnapshot? snapshot =
                await _snapshotStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is not null)
            {
                foreach (PersistedMetadataVector metadata in snapshot.Metadata)
                {
                    string expectedHash =
                        MetadataProjectionIdentity.ComputeCanonicalSha256(
                            metadata.Record);
                    if (!string.Equals(
                            metadata.ProjectionHash,
                            expectedHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Metadata '{metadata.Record.Id}' has a stale projection hash.");
                    }
                }
            }

            return snapshot;
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidDataException)
        {
            LastSnapshotLoadError =
                $"Ignored invalid semantic snapshot: {exception.GetType().Name}.";
            return null;
        }
    }

    private async ValueTask<int> EmbedMetadataAsync(
        List<PendingMetadata> pending,
        List<PersistedMetadataVector> destination,
        CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < pending.Count; offset += _embeddingBatchSize)
        {
            int count = Math.Min(_embeddingBatchSize, pending.Count - offset);
            List<PendingMetadata> batch = pending.GetRange(offset, count);
            GeneratedEmbeddings<Embedding<float>> vectors =
                await _embeddingGenerator.GenerateAsync(
                    batch.Select(static item => item.Text),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            if (vectors.Count != batch.Count)
            {
                throw new InvalidDataException("Embedding output count did not match metadata input count.");
            }

            for (int index = 0; index < batch.Count; index++)
            {
                destination.Add(new PersistedMetadataVector
                {
                    Record = batch[index].Record,
                    ProjectionHash = batch[index].ProjectionHash,
                    Vector = vectors[index].Vector.ToArray(),
                });
            }
        }

        return pending.Count;
    }

    private async ValueTask<int> EmbedContentAsync(
        List<PendingContent> pending,
        List<PersistedContentVector> destination,
        CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < pending.Count; offset += _embeddingBatchSize)
        {
            int count = Math.Min(_embeddingBatchSize, pending.Count - offset);
            List<PendingContent> batch = pending.GetRange(offset, count);
            GeneratedEmbeddings<Embedding<float>> vectors =
                await _embeddingGenerator.GenerateAsync(
                    batch.Select(static item => item.Chunk.Text),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            if (vectors.Count != batch.Count)
            {
                throw new InvalidDataException("Embedding output count did not match content input count.");
            }

            for (int index = 0; index < batch.Count; index++)
            {
                destination.Add(new PersistedContentVector
                {
                    Chunk = batch[index].Chunk,
                    Vector = vectors[index].Vector.ToArray(),
                });
            }
        }

        return pending.Count;
    }

    private sealed record PendingMetadata(
        MetadataRecord Record,
        string Text,
        string ProjectionHash);

    private sealed record PendingContent(ContentChunk Chunk);
}
