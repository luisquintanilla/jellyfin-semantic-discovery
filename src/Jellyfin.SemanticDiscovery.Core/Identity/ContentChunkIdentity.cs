using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Identity;

public static class ContentChunkIdentity
{
    public static string Compute(
        Guid ownerId,
        string sourcePath,
        ContentSourceKind sourceKind,
        int ordinal,
        string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        return ContentHash.Compute(
            $"{ownerId:N}\n{sourcePath}\n{sourceKind}\n{ordinal}\n{contentHash}");
    }
}
