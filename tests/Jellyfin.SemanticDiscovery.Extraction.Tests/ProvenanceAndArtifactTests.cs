using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
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
        ExtractionArtifactBundle bundle =
            await IngestionArtifactBuilder.BuildAsync(
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
            new ApplicationChunkTokenizer(),
            CancellationToken.None);
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
        Assert.Equal(
            ApplicationChunkTokenizer.IdentityHash,
            root.GetProperty("tokenizerIdentity").GetString());
        Assert.Equal(
            SemanticConstants.DocumentChunkingIdentity,
            root.GetProperty("chunkingIdentity").GetString());
        JsonElement chunk = root.GetProperty("chunks")[0];
        Assert.Equal(0, chunk.GetProperty("ordinal").GetInt32());
        Assert.Equal(
            "# Heading\nPage text",
            chunk.GetProperty("text").GetString());
        Assert.Equal(1, chunk.GetProperty("pageNumber").GetInt32());
        Assert.InRange(
            chunk.GetProperty("tokenCount").GetInt32(),
            1,
            SemanticConstants.MaximumChunkTokens);
        Assert.Equal(
            2,
            chunk.GetProperty("provenance").GetArrayLength());
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HeaderChunkerArtifactsArePageScopedAndCarryHeadingAndProvenance()
    {
        ExtractionArtifactBundle bundle = await IngestionArtifactBuilder.BuildAsync(
            CreateJob(),
            [
                new ExtractedPage(
                    1,
                    [
                        new(1, "Intro", "Intro", null, null, ["text-layer"]),
                        new(1, "First page body.", "Intro", null, null, ["text-layer"]),
                        new(1, "Diagram caption.", "Decision", null, null, ["visual:model"]),
                    ]),
                new ExtractedPage(
                    2,
                    [new(2, "Continued decision text.", null, null, null, ["text-layer"])]),
            ],
            new ApplicationChunkTokenizer(),
            CancellationToken.None);

        IReadOnlyList<ExtractionArtifactChunk> chunks = bundle.Artifact.Chunks;
        Assert.Equal(2, bundle.Documents.Count);
        Assert.Collection(
            chunks,
            chunk =>
            {
                Assert.Equal("# Intro\nFirst page body.", chunk.Text);
                Assert.Equal("Intro", chunk.Heading);
                Assert.Equal(1, chunk.PageNumber);
                Assert.Equal(["text-layer"], chunk.Provenance);
            },
            chunk =>
            {
                Assert.Equal("# Decision\nDiagram caption.", chunk.Text);
                Assert.Equal("Decision", chunk.Heading);
                Assert.Equal(1, chunk.PageNumber);
                Assert.Equal(["visual:model"], chunk.Provenance);
            },
            chunk =>
            {
                Assert.Equal("# Decision\nContinued decision text.", chunk.Text);
                Assert.Equal("Decision", chunk.Heading);
                Assert.Equal(2, chunk.PageNumber);
                Assert.Equal(["text-layer"], chunk.Provenance);
            });
    }

    [Fact]
    public async Task OversizedParagraphIsSplitWithinTheTokenLimit()
    {
        string paragraph = string.Join(
            "\n",
            Enumerable.Range(0, 300).Select(static line => $"Line {line} of a long page."));
        ApplicationChunkTokenizer tokenizer = new();

        ExtractionArtifactBundle bundle = await IngestionArtifactBuilder.BuildAsync(
            CreateJob(),
            [new ExtractedPage(3, [new(3, paragraph, "Long", null, null, ["text-layer"])])],
            tokenizer,
            CancellationToken.None);

        IReadOnlyList<ExtractionArtifactChunk> chunks = bundle.Artifact.Chunks;
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.StartsWith("# Long\n", chunk.Text, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', chunk.Text);
            Assert.InRange(chunk.TokenCount, 1, SemanticConstants.MaximumChunkTokens);
            Assert.Equal(3, chunk.PageNumber);
            Assert.Equal("Long", chunk.Heading);
        });
        Assert.Equal(
            tokenizer.NormalizeWithSourceOffsets(paragraph).Text,
            tokenizer.NormalizeWithSourceOffsets(string.Join(
                "\n",
                chunks.Select(static chunk => chunk.Text["# Long\n".Length..]))).Text);
    }

    private static ExtractionJob CreateJob()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            "job",
            "manifest-item",
            new string('a', 64),
            ExtractionProfile.Hybrid,
            "model",
            ExtractionJobState.Running,
            now,
            now);
    }
}