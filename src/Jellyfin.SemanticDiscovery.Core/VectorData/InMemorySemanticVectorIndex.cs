using CommunityToolkit.VectorData.InMemory;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Microsoft.Extensions.VectorData;

namespace Jellyfin.SemanticDiscovery.Core.VectorData;

public sealed class InMemorySemanticVectorIndex : IVectorSearchIndex
{
    private State _state = State.Empty;

    public async ValueTask ReplaceAsync(
        IndexSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        PreparedState replacement =
            await PrepareAsync(snapshot, cancellationToken).ConfigureAwait(false);
        Publish(replacement);
    }

    internal async ValueTask<PreparedState> PrepareAsync(
        IndexSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        IndexSnapshotValidator.Validate(snapshot);
        State replacement = await State.CreateAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
        return new PreparedState(this, replacement);
    }

    internal void Publish(PreparedState prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Owner, this))
        {
            throw new InvalidOperationException(
                "A prepared vector state can only be published by its originating index.");
        }

        Volatile.Write(ref _state, prepared.Value);
    }

    public IVectorSearchReadSession OpenReadSession() =>
        new ReadSession(Volatile.Read(ref _state));

    private static void ValidateSearch(ReadOnlyMemory<float> queryVector, int count)
    {
        if (queryVector.Length != SemanticConstants.EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"Query vectors must contain {SemanticConstants.EmbeddingDimensions} values.",
                nameof(queryVector));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        bool nonZero = false;
        foreach (float value in queryVector.Span)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException(
                    "Query vectors must contain only finite values.",
                    nameof(queryVector));
            }

            nonZero |= value != 0F;
        }

        if (!nonZero)
        {
            throw new ArgumentException(
                "Query vectors must not be the zero vector.",
                nameof(queryVector));
        }
    }

    internal sealed record State(
        InMemoryCollection<Guid, MetadataVectorDocument> Metadata,
        InMemoryCollection<string, ContentVectorDocument> Content,
        Dictionary<Guid, MetadataRecord> Records,
        Dictionary<Guid, float[]> Vectors)
    {
        public static State Empty { get; } = CreateEmpty();

        public static async ValueTask<State> CreateAsync(
            IndexSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            State state = CreateEmpty();
            await state.Metadata.EnsureCollectionExistsAsync(cancellationToken)
                .ConfigureAwait(false);
            await state.Content.EnsureCollectionExistsAsync(cancellationToken)
                .ConfigureAwait(false);

            PersistedMetadataVector[] metadataEntries = snapshot.Metadata
                .Select(static item => new PersistedMetadataVector
                {
                    Record = CloneRecord(item.Record),
                    ProjectionHash = item.ProjectionHash,
                    Vector = [.. item.Vector],
                })
                .ToArray();
            MetadataVectorDocument[] metadata = metadataEntries
                .Select(static item => MetadataVectorDocument.From(
                    item.Record,
                    item.ProjectionHash,
                    item.Vector))
                .ToArray();
            ContentVectorDocument[] content = snapshot.Content
                .Select(static item => ContentVectorDocument.From(
                    item.Chunk,
                    item.Vector))
                .ToArray();

            if (metadata.Length > 0)
            {
                await state.Metadata.UpsertAsync(metadata, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (content.Length > 0)
            {
                await state.Content.UpsertAsync(content, cancellationToken)
                    .ConfigureAwait(false);
            }

            return state with
            {
                Records = metadataEntries.ToDictionary(
                    static item => item.Record.Id,
                    static item => item.Record),
                Vectors = metadataEntries.ToDictionary(
                    static item => item.Record.Id,
                    static item => item.Vector),
            };
        }

        private static MetadataRecord CloneRecord(MetadataRecord record) => record with
        {
            AncestorIds = [.. record.AncestorIds],
            Genres = [.. record.Genres],
            Tags = [.. record.Tags],
            People = [.. record.People],
            Studios = [.. record.Studios],
        };

        private static State CreateEmpty()
        {
            State state = new(
                new InMemoryCollection<Guid, MetadataVectorDocument>("metadata"),
                new InMemoryCollection<string, ContentVectorDocument>("content"),
                new Dictionary<Guid, MetadataRecord>(),
                new Dictionary<Guid, float[]>());
            state.Metadata.EnsureCollectionExistsAsync().GetAwaiter().GetResult();
            state.Content.EnsureCollectionExistsAsync().GetAwaiter().GetResult();
            return state;
        }
    }

    internal sealed class PreparedState(
        InMemorySemanticVectorIndex owner,
        State value)
    {
        internal InMemorySemanticVectorIndex Owner { get; } = owner;

        private State StateValue { get; } = value;

        private int _published;

        internal State Value
        {
            get
            {
                if (Interlocked.Exchange(ref _published, 1) != 0)
                {
                    throw new InvalidOperationException(
                        "A prepared vector state cannot be published more than once.");
                }

                return StateValue;
            }
        }
    }

    private sealed class ReadSession(State state) : IVectorSearchReadSession
    {
        public async ValueTask<IReadOnlyList<MetadataMatch>> SearchMetadataAsync(
            ReadOnlyMemory<float> queryVector,
            int count,
            IReadOnlySet<Guid> allowedItemIds,
            CancellationToken cancellationToken)
        {
            ValidateSearch(queryVector, count);
            ArgumentNullException.ThrowIfNull(allowedItemIds);
            List<MetadataMatch> matches = new(count);
            int rank = 0;
            await foreach (VectorSearchResult<MetadataVectorDocument> result in
                           state.Metadata.SearchAsync(
                               queryVector,
                               count,
                               new VectorSearchOptions<MetadataVectorDocument>
                               {
                                   Filter = document => allowedItemIds.Contains(document.Id),
                               },
                               cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                matches.Add(new MetadataMatch(
                    result.Record.ToRecord(),
                    ConvertScore(result.Score),
                    rank++));
            }

            return matches;
        }

        public async ValueTask<IReadOnlyList<ContentMatch>> SearchContentAsync(
            ReadOnlyMemory<float> queryVector,
            int count,
            IReadOnlySet<Guid> allowedOwnerIds,
            CancellationToken cancellationToken)
        {
            ValidateSearch(queryVector, count);
            ArgumentNullException.ThrowIfNull(allowedOwnerIds);
            List<ContentMatch> matches = new(count);
            int rank = 0;
            await foreach (VectorSearchResult<ContentVectorDocument> result in
                           state.Content.SearchAsync(
                               queryVector,
                               count,
                               new VectorSearchOptions<ContentVectorDocument>
                               {
                                   Filter = document =>
                                       allowedOwnerIds.Contains(document.OwnerId),
                               },
                               cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                matches.Add(new ContentMatch(
                    result.Record.ToChunk(),
                    ConvertScore(result.Score),
                    rank++));
            }

            return matches;
        }

        public ValueTask<IReadOnlyList<MetadataRecord>> GetMetadataRecordsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MetadataRecord> records = state.Records.Values
                .Select(CloneRecord)
                .ToArray();
            return ValueTask.FromResult(records);
        }

        public ValueTask<MetadataRecord?> GetMetadataAsync(
            Guid itemId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Records.TryGetValue(itemId, out MetadataRecord? record);
            return ValueTask.FromResult(record is null ? null : CloneRecord(record));
        }

        public ValueTask<ReadOnlyMemory<float>?> GetMetadataVectorAsync(
            Guid itemId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<float>? vector =
                state.Vectors.TryGetValue(itemId, out float[]? value)
                    ? (ReadOnlyMemory<float>?)value.ToArray()
                    : null;
            return ValueTask.FromResult(vector);
        }

    }

    internal static float ConvertScore(double? score)
    {
        if (!score.HasValue || !double.IsFinite(score.Value))
        {
            throw new InvalidOperationException(
                "The vector store returned a missing or non-finite similarity score.");
        }

        float converted = (float)score.Value;
        if (!float.IsFinite(converted) || converted is < -1.0001F or > 1.0001F)
        {
            throw new InvalidOperationException(
                "The vector store returned an out-of-range similarity score.");
        }

        return converted;
    }

    private static MetadataRecord CloneRecord(MetadataRecord record) => record with
    {
        AncestorIds = [.. record.AncestorIds],
        Genres = [.. record.Genres],
        Tags = [.. record.Tags],
        People = [.. record.People],
        Studios = [.. record.Studios],
    };
}
