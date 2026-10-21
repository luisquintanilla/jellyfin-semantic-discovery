namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

/// <summary>
/// Locates the pinned all-MiniLM-L6-v2 model assets under the repository's
/// <c>.data/models/all-MiniLM-L6-v2</c> directory. This suite never downloads
/// the model itself; it only observes assets that were installed ahead of time
/// (for example by <c>dotnet run --project src/Jellyfin.SemanticDiscovery.Setup -- model</c>
/// or <c>scripts/Smoke-Plugin.ps1</c>).
/// </summary>
internal static class MiniLmModelLocator
{
    private const string ModelFileName = "model.onnx";
    private const string VocabularyFileName = "vocab.txt";

    public static string? ModelDirectory { get; } = Locate();

    public static bool IsAvailable => ModelDirectory is not null;

    public static string SkipReason { get; } =
        "The pinned all-MiniLM-L6-v2 assets were not found under '.data/models/all-MiniLM-L6-v2'. " +
        "Install them first (dotnet run --project src/Jellyfin.SemanticDiscovery.Setup -- model, " +
        "or scripts/Smoke-Plugin.ps1) before running this test; it never downloads from the network.";

    private static string? Locate()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                ".data",
                "models",
                "all-MiniLM-L6-v2");
            if (File.Exists(Path.Combine(candidate, ModelFileName)) &&
                File.Exists(Path.Combine(candidate, VocabularyFileName)))
            {
                return candidate;
            }
        }

        return null;
    }
}
