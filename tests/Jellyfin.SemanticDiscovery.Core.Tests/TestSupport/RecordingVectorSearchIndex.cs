using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;

namespace Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

/// <summary>
/// A scripted, recording <see cref="IVectorSearchIndex"/> for
/// <see cref="SemanticDiscoveryService"/> tests. Search results are returned
/// verbatim from <see cref="MetadataResults"/>/<see cref="ContentResults"/>
/// regardless of the query vector, so tests can assert on call shape
/// (vector length, requested count, call count) independently from fusion
/// behavior.
/// </summary>
public sealed class RecordingVectorSearchIndex :
    IVectorSearchIndex,
    IVectorSearchReadSession
{
    public List<(ReadOnlyMemory<float> Vector, int Count, IReadOnlySet<Guid> AllowedIds)>
        MetadataSearchCalls { get; } = [];

    public List<(ReadOnlyMemory<float> Vector, int Count, IReadOnlySet<Guid> AllowedIds)>
        ContentSearchCalls { get; } = [];

    public List<Guid> GetMetadataCalls { get; } = [];

    public List<Guid> GetMetadataVectorCalls { get; } = [];

    public IReadOnlyList<MetadataMatch> MetadataResults { get; set; } = [];

    public IReadOnlyList<ContentMatch> ContentResults { get; set; } = [];

    public Dictionary<Guid, MetadataRecord> Records { get; } = [];

    public Dictionary<Guid, float[]> Vectors { get; } = [];

    public IVectorSearchReadSession OpenReadSession() => this;

    public ValueTask<IReadOnlyList<MetadataMatch>> SearchMetadataAsync(
        ReadOnlyMemory<float> queryVector,
        int count,
        IReadOnlySet<Guid> allowedItemIds,
        CancellationToken cancellationToken)
    {
        MetadataSearchCalls.Add((queryVector, count, allowedItemIds));
        return ValueTask.FromResult<IReadOnlyList<MetadataMatch>>(
            MetadataResults.Where(match => allowedItemIds.Contains(match.Record.Id)).ToArray());
    }

    public ValueTask<IReadOnlyList<ContentMatch>> SearchContentAsync(
        ReadOnlyMemory<float> queryVector,
        int count,
        IReadOnlySet<Guid> allowedOwnerIds,
        CancellationToken cancellationToken)
    {
        ContentSearchCalls.Add((queryVector, count, allowedOwnerIds));
        return ValueTask.FromResult<IReadOnlyList<ContentMatch>>(
            ContentResults.Where(match => allowedOwnerIds.Contains(match.Chunk.OwnerId)).ToArray());
    }

    public ValueTask<IReadOnlyList<MetadataRecord>> GetMetadataRecordsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MetadataRecord> records = MetadataResults
            .Select(static match => match.Record)
            .Concat(Records.Values)
            .DistinctBy(static record => record.Id)
            .ToArray();
        return ValueTask.FromResult(records);
    }

    public ValueTask<MetadataRecord?> GetMetadataAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        GetMetadataCalls.Add(itemId);
        return ValueTask.FromResult(
            Records.TryGetValue(itemId, out MetadataRecord? record) ? record : null);
    }

    public ValueTask<ReadOnlyMemory<float>?> GetMetadataVectorAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        GetMetadataVectorCalls.Add(itemId);
        return ValueTask.FromResult(
            Vectors.TryGetValue(itemId, out float[]? vector)
                ? (ReadOnlyMemory<float>?)vector
                : null);
    }
}
