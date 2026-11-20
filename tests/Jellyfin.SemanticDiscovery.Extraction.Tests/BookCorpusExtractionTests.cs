using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Extraction;
using Jellyfin.SemanticDiscovery.Extraction.Manifest;
using Jellyfin.SemanticDiscovery.Extraction.Pdf;
using Jellyfin.SemanticDiscovery.Extraction.Validation;

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
            string path = ResolveSourcePath(root, item);
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

    [Fact]
    public async Task RealCorpusPdfUsesHeadingAwareTokenBoundedChunks()
    {
        string root = FindRepositoryRoot();
        using JsonDocument manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(root, "assets", "demo", "manifest.json")));
        PdfPigTextLayerExtractor textExtractor = new();
        using TestDirectory state = new();
        DocumentExtractionPipeline pipeline = new(
            textExtractor,
            new UnusedRasterizer(),
            new UnusedVisionExtractor(),
            new ProvenanceMerger(),
            new ExtractionOptions { StateRoot = state.Path },
            new ApplicationChunkTokenizer());

        foreach (JsonElement item in manifest.RootElement
                     .GetProperty("items")
                     .EnumerateArray())
        {
            string sourcePath = ResolveSourcePath(root, item);
            string itemId = item.GetProperty("id").GetString()!;
            string sourceHash =
                item.GetProperty("sourceSha256").GetString()!;
            int pageCount = item.GetProperty("pageCount").GetInt32();
            ExtractionManifestItem manifestItem = new(
                itemId,
                item.GetProperty("sourcePath").GetString()!,
                sourceHash,
                item.GetProperty("sourceSizeBytes").GetInt64(),
                pageCount);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ExtractionJob job = new(
                $"job-{itemId}",
                itemId,
                sourceHash,
                ExtractionProfile.Text,
                "qwen3-vl:8b-instruct",
                ExtractionJobState.Running,
                now,
                now);

            ExtractionArtifactBundle bundle = await pipeline.ExtractAsync(
                job,
                new ValidatedExtractionSource(
                    manifestItem,
                    sourcePath,
                    sourceHash,
                    pageCount),
                CancellationToken.None);

            Assert.NotEmpty(bundle.Artifact.Chunks);
            Assert.All(bundle.Artifact.Chunks, static chunk =>
            {
                Assert.InRange(
                    chunk.TokenCount,
                    1,
                    SemanticConstants.MaximumChunkTokens);
                Assert.False(string.IsNullOrWhiteSpace(chunk.Heading));
                Assert.NotNull(chunk.PageNumber);
                Assert.Contains("text-layer", chunk.Provenance);
            });
        }
    }

    private static string ResolveSourcePath(
        string repositoryRoot,
        JsonElement item) =>
        Path.Combine(
            repositoryRoot,
            "assets",
            "demo",
            item.GetProperty("sourcePath").GetString()!
                .Replace('/', Path.DirectorySeparatorChar));

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

    private sealed class UnusedRasterizer : IPdfRasterizer
    {
        public ValueTask<RasterizedPdfPage> RasterizeAsync(
            string sourcePath,
            int pageNumber,
            int dpi,
            string workspace,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedVisionExtractor : IVisionPageExtractor
    {
        public ValueTask<IReadOnlyList<ExtractedFragment>> ExtractAsync(
            RasterizedPdfPage page,
            string model,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
