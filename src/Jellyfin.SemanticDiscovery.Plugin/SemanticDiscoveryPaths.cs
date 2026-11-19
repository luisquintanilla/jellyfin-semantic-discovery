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

    public static Uri GetOllamaEndpoint(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new Uri(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable(
                    "SEMANTIC_DISCOVERY_OLLAMA_ENDPOINT"),
                configuration.OllamaEndpoint,
                "http://127.0.0.1:11434"),
            UriKind.Absolute);
    }

    public static string GetEmbeddingModel(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable(
                "SEMANTIC_DISCOVERY_EMBEDDING_MODEL"),
            configuration.EmbeddingModel,
            Core.Embedding.OllamaEmbeddingOptions.DefaultModel);
    }

    public static string GetExtractionModel(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable(
                "SEMANTIC_DISCOVERY_EXTRACTION_MODEL"),
            configuration.ExtractionModel,
            "qwen3-vl:8b-instruct");
    }

    public static string GetExtractionMode(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string mode = FirstNonEmpty(
            Environment.GetEnvironmentVariable(
                "SEMANTIC_DISCOVERY_EXTRACTION_MODE"),
            configuration.ExtractionMode,
            "Hybrid");
        return mode is "Text" or "Vision" or "Hybrid"
            ? mode
            : throw new InvalidOperationException(
                "Extraction mode must be Text, Vision, or Hybrid.");
    }

    public string GetExtractionArtifactRoot(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Path.GetFullPath(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable(
                    "SEMANTIC_DISCOVERY_EXTRACTION_ARTIFACT_ROOT"),
                configuration.ExtractionArtifactRoot,
                Path.Combine(_dataPath, "extractions", "published")));
    }

    public string GetSnapshotPath(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_SNAPSHOT_PATH"),
            configuration.SnapshotPath,
            Path.Combine(_dataPath, "index.json"));
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.First(static value => !string.IsNullOrWhiteSpace(value))!;
}
