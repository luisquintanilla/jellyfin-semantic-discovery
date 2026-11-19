using System.Text.Json;
using Jellyfin.SemanticDiscovery.Extraction.Pdf;

namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

public sealed class BookCorpusExtractionTests
{
    [Fact]
    public async Task SealedBooksExposeEightPageTextLayersAndAnchors()
    {
        string root = FindRepositoryRoot();
        using JsonDocument manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(root, "assets", "demo", "manifest.json")));
        using JsonDocument benchmark = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(
                    root,
                    "assets",
                    "demo",
                    "extraction-benchmark.json")));
        Dictionary<string, JsonElement> items = manifest.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString()!,
                static item => item.Clone(),
                StringComparer.Ordinal);
        PdfPigTextLayerExtractor extractor = new();

        foreach (JsonElement check in benchmark.RootElement
                     .GetProperty("checks")
                     .EnumerateArray())
        {
            string itemId =
                check.GetProperty("manifestItemId").GetString()!;
            JsonElement item = items[itemId];
            string path = Path.Combine(
                root,
                "assets",
                "demo",
                item.GetProperty("sourcePath").GetString()!
                    .Replace('/', Path.DirectorySeparatorChar));
            int expectedPages =
                check.GetProperty("expectedPages").GetInt32();

            Assert.Equal(
                expectedPages,
                await extractor.GetPageCountAsync(
                    path,
                    CancellationToken.None));
            IReadOnlyList<PdfTextPage> pages =
                await extractor.ExtractAsync(
                    path,
                    expectedPages,
                    CancellationToken.None);
            string text = string.Join(
                ' ',
                pages.Select(static page => Normalize(page.Text)));
            Assert.Contains(
                Normalize(check.GetProperty("textAnchor").GetString()!),
                text,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Normalize(string value) =>
        string.Join(
            ' ',
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));

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
