using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Core.Projection;

public static class MetadataProjectionIdentity
{
    public const string ContractId =
        "metadata-projector-v1;unicode-form-kc;unicode-whitespace-ascii-space;" +
        "full-canonical-text-sha256-v1";

    public static string CreateCanonicalText(MetadataRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TextNormalization.NormalizeWhitespace(
            MetadataProjector.Project(record));
    }

    public static string ComputeCanonicalSha256(MetadataRecord record) =>
        ContentHash.Compute(CreateCanonicalText(record));
}
