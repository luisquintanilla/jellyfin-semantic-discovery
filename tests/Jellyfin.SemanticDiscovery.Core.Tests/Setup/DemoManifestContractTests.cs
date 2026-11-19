using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Setup;

public sealed class DemoManifestContractTests
{
    [Fact]
    public async Task DemoManifestContainsThreeValidatedItemsPerMediaKind()
    {
        string repositoryRoot = FindRepositoryRoot();
        string demoRoot = Path.Combine(repositoryRoot, "assets", "demo");
        string path = Path.Combine(demoRoot, "manifest.json");
        using JsonDocument document =
            JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;

        Assert.Equal("demo-v1", root.GetProperty("profileId").GetString());
        Assert.Equal(
            "ready",
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
        foreach (JsonElement asset in assets)
        {
            string mediaPath = Path.Combine(
                demoRoot,
                asset.GetProperty("media").GetString()!
                    .Replace('/', Path.DirectorySeparatorChar));
            FileInfo media = new(mediaPath);
            Assert.True(media.Exists);
            Assert.Equal(asset.GetProperty("bytes").GetInt64(), media.Length);
            await using FileStream stream = media.OpenRead();
            string hash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream));
            Assert.Equal(asset.GetProperty("sha256").GetString(), hash);
            string metadataPath = Path.Combine(
                demoRoot,
                asset.GetProperty("metadata").GetString()!
                    .Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(metadataPath));
            if (asset.GetProperty("mediaKind").GetString() is
                "Movie" or "Audio")
            {
                string transcriptPath = Path.Combine(
                    demoRoot,
                    asset.GetProperty("transcript").GetString()!
                        .Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(transcriptPath));
                Assert.Equal(
                    asset.GetProperty("metadataSha256").GetString(),
                    await ComputeSha256Async(metadataPath));
                Assert.Equal(
                    asset.GetProperty("transcriptSha256").GetString(),
                    await ComputeSha256Async(transcriptPath));
            }
            else
            {
                Assert.Equal(8, asset.GetProperty("pageCount").GetInt32());
            }
        }

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

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream));
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
