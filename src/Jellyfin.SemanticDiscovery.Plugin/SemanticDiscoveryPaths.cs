using MediaBrowser.Common.Configuration;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class SemanticDiscoveryPaths
{
    private readonly string _dataPath;

    public SemanticDiscoveryPaths(IApplicationPaths applicationPaths)
        : this((applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths))).DataPath)
    {
    }

    private SemanticDiscoveryPaths(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        _dataPath = Path.Combine(applicationDataPath, "semantic-discovery");
    }

    public static SemanticDiscoveryPaths FromApplicationDataPath(string applicationDataPath) =>
        new(applicationDataPath);

    public string GetModelDirectory(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_MODEL_DIRECTORY"),
            configuration.ModelDirectory,
            Path.Combine(_dataPath, "models", "all-MiniLM-L6-v2"));
    }

    public string GetSnapshotPath(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_SNAPSHOT_PATH"),
            configuration.SnapshotPath,
            Path.Combine(_dataPath, "index.json"));
    }

    public static Uri GetMarkItDownEndpoint(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string value = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_MARKITDOWN_ENDPOINT"),
            configuration.MarkItDownEndpoint,
            "http://127.0.0.1:3001/mcp");
        return new Uri(value, UriKind.Absolute);
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.First(static value => !string.IsNullOrWhiteSpace(value))!;
}
