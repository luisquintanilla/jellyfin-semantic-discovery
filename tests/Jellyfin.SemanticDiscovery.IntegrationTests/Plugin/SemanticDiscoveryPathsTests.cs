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
            new Uri("http://127.0.0.1:11434"),
            SemanticDiscoveryPaths.GetOllamaEndpoint(configuration));
        Assert.Equal(
            "embeddinggemma:latest",
            SemanticDiscoveryPaths.GetEmbeddingModel(configuration));
        Assert.Equal(
            "qwen3-vl:8b",
            SemanticDiscoveryPaths.GetExtractionModel(configuration));
        Assert.Equal(
            "Hybrid",
            SemanticDiscoveryPaths.GetExtractionMode(configuration));
        Assert.Equal(
            Path.Combine(applicationDataPath, "semantic-discovery", "index.json"),
            paths.GetSnapshotPath(configuration));
        Assert.Equal(
            Path.Combine(
                applicationDataPath,
                "semantic-discovery",
                "extractions",
                "published"),
            paths.GetExtractionArtifactRoot(configuration));
        Assert.DoesNotContain("/config", configuration.SnapshotPath, StringComparison.Ordinal);
        Assert.Empty(configuration.OllamaEndpoint);
    }
}
