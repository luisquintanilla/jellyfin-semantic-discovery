using Jellyfin.SemanticDiscovery.Plugin;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Plugin;

public sealed class SemanticDiscoveryPathsTests
{
    [Fact]
    public void EmptyConfigurationDerivesStorageFromApplicationDataPath()
    {
        string applicationDataPath = Path.Combine(
            Path.GetTempPath(),
            "jellyfin-semantic-path-test");
        SemanticDiscoveryPaths paths =
            SemanticDiscoveryPaths.FromApplicationDataPath(applicationDataPath);
        PluginConfiguration configuration = new();

        Assert.Equal(
            Path.Combine(
                applicationDataPath,
                "semantic-discovery",
                "models",
                "all-MiniLM-L6-v2"),
            paths.GetModelDirectory(configuration));
        Assert.Equal(
            Path.Combine(applicationDataPath, "semantic-discovery", "index.json"),
            paths.GetSnapshotPath(configuration));
        Assert.DoesNotContain("/config", configuration.SnapshotPath, StringComparison.Ordinal);
        Assert.DoesNotContain("/models", configuration.ModelDirectory, StringComparison.Ordinal);
    }
}
