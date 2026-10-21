using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

public interface IMediaAccessPolicy
{
    ValueTask<IReadOnlySet<Guid>> GetAccessibleItemIdsAsync(
        IReadOnlyList<MetadataRecord> candidates,
        SearchScope scope,
        CancellationToken cancellationToken);
}
