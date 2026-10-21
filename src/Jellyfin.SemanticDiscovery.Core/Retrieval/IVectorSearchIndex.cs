using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

public interface IVectorSearchIndex
{
    IVectorSearchReadSession OpenReadSession();
}

public interface IVectorSearchReadSession
{
    ValueTask<IReadOnlyList<MetadataMatch>> SearchMetadataAsync(
        ReadOnlyMemory<float> queryVector,
        int count,
        IReadOnlySet<Guid> allowedItemIds,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ContentMatch>> SearchContentAsync(
        ReadOnlyMemory<float> queryVector,
        int count,
        IReadOnlySet<Guid> allowedOwnerIds,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<MetadataRecord>> GetMetadataRecordsAsync(
        CancellationToken cancellationToken);

    ValueTask<MetadataRecord?> GetMetadataAsync(
        Guid itemId,
        CancellationToken cancellationToken);

    ValueTask<ReadOnlyMemory<float>?> GetMetadataVectorAsync(
        Guid itemId,
        CancellationToken cancellationToken);
}
