using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Extraction.Artifacts;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Extraction;
using Jellyfin.SemanticDiscovery.Extraction.Persistence;

namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

public sealed class ProvenanceAndArtifactTests
{
    [Fact]
    public void MergeDeduplicatesTextAndRetainsTextAndVisualProvenance()
    {
        ProvenanceMerger merger = new();

        ExtractedPage page = merger.Merge(
            1,
            [new(1, "Alpha   beta", null, null, null, ["text-layer"])],
            [
                new(
                    1,
                    "alpha beta",
                    null,
                    null,
                    null,
                    ["visual:model"]),
                new(
                    1,
                    "Diagram label",
                    null,
                    null,
                    null,
                    ["visual:model"]),
            ]);

        Assert.Equal(2, page.Fragments.Count);
        Assert.Equal(
            ["text-layer", "visual:model"],
            page.Fragments[0].Provenance);
        Assert.Equal("Diagram label", page.Fragments[1].Text);
    }

    [Fact]
    public async Task PublishesPluginContractByLowercaseSourceHashAtomically()
    {
        using TestDirectory directory = new();
        string hash = new('a', 64);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExtractionJob job = new(
            "job",
            "manifest-item",
            hash.ToUpperInvariant(),
            ExtractionProfile.Hybrid,
            "explicit-model",
            ExtractionJobState.Running,
            now,
            now);
        ExtractionArtifactBundle bundle = IngestionArtifactBuilder.Build(
            job,
            [
                new ExtractedPage(
                    1,
                    [
                        new(
                            1,
                            "Page text",
                            "Heading",
                            null,
                            null,
                            ["text-layer", "visual:model"]),
                    ]),
            ],
            maximumChunkCharacters: 100);
        FileCompletedArtifactPublisher publisher = new(
            directory.Path,
            new AtomicJsonFile(new AtomicFileOperations()));

        string path = await publisher.PublishAsync(
            bundle,
            CancellationToken.None);

        string modelHash = Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes("explicit-model")))[..16];
        Assert.Equal(
            directory.GetPath(
                $"{hash}.hybrid.{modelHash}.json"),
            path);
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path));
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "manifest-item",
            root.GetProperty("manifestItemId").GetString());
        Assert.Equal(hash, root.GetProperty("sourceSha256").GetString());
        Assert.Equal("Hybrid", root.GetProperty("extractionProfile").GetString());
        Assert.Equal("explicit-model", root.GetProperty("model").GetString());
        JsonElement chunk = root.GetProperty("chunks")[0];
        Assert.Equal(0, chunk.GetProperty("ordinal").GetInt32());
        Assert.Equal("Page text", chunk.GetProperty("text").GetString());
        Assert.Equal(1, chunk.GetProperty("pageNumber").GetInt32());
        Assert.Equal(
            2,
            chunk.GetProperty("provenance").GetArrayLength());
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            "*.tmp",
            SearchOption.AllDirectories));
    }
}
