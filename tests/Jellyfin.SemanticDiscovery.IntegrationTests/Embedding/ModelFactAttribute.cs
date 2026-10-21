namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

/// <summary>
/// A fact that requires the pinned all-MiniLM-L6-v2 assets to already be present
/// on disk. Skips (rather than fails or downloads) when the assets are missing.
/// </summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        bool modelRequired = string.Equals(
            Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_REQUIRE_MODEL"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!MiniLmModelLocator.IsAvailable && !modelRequired)
        {
            Skip = MiniLmModelLocator.SkipReason;
        }
    }
}
