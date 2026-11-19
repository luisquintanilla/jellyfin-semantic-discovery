using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Setup;

public sealed class DemoManifestContractTests
{
    [Fact]
    public void DemoManifestReservesExactlyThreeItemsPerPublicMediaKind()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "assets",
            "demo",
            "manifest.json");
        using JsonDocument document =
            JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;

        Assert.Equal("demo-v1", root.GetProperty("profileId").GetString());
        Assert.Equal(
            "pending-final-assets",
            root.GetProperty("deliveryStatus").GetString());
        JsonElement[] assets = root.GetProperty("assets")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(9, assets.Length);
        Dictionary<string, int> counts = assets
            .GroupBy(asset =>
                asset.GetProperty("mediaKind").GetString()!)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        Assert.Equal(3, counts["Audio"]);
        Assert.Equal(3, counts["Book"]);
        Assert.Equal(3, counts["Movie"]);
        Assert.All(assets, static asset =>
        {
            Assert.False(asset.TryGetProperty("prompt", out _));
            Assert.False(asset.TryGetProperty("storyboard", out _));
            Assert.False(asset.TryGetProperty("generator", out _));
            Assert.False(asset.TryGetProperty("receipt", out _));
        });
        Assert.All(
            assets.Where(asset =>
                asset.GetProperty("mediaKind").GetString() is
                    "Movie" or "Audio"),
            static asset => Assert.Equal(
                JsonValueKind.Null,
                asset.GetProperty("sha256").ValueKind));
        Assert.All(
            assets.Where(asset =>
                asset.GetProperty("mediaKind").GetString() == "Book"),
            static asset =>
            {
                Assert.Equal(
                    64,
                    asset.GetProperty("sha256").GetString()!.Length);
                Assert.Equal(8, asset.GetProperty("pageCount").GetInt32());
            });

        JsonElement[] libraries = root.GetProperty("libraries")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, libraries.Length);
        Assert.All(
            libraries,
            static library => Assert.Equal(
                3,
                library.GetProperty("items").GetArrayLength()));
        Assert.Equal(
            3,
            root.GetProperty("items").GetArrayLength());
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "Jellyfin.SemanticDiscovery.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "The repository root could not be located.");
    }
}
