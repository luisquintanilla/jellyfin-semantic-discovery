namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "test-runs",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(params string[] segments) =>
        segments.Aggregate(Path, System.IO.Path.Combine);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
