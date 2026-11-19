using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Indexing;

public sealed class PublishedExtractionContentReaderTests : IDisposable
{
    private static readonly string[] ExpectedProvenance =
        ["text-layer", "visual:qwen3-vl:8b"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"published-extraction-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadsOnlyArtifactMatchingSourceModeAndModel()
    {
        Directory.CreateDirectory(_root);
        string sourcePath = Path.Combine(_root, "book.pdf");
        await File.WriteAllBytesAsync(
            sourcePath,
            Encoding.UTF8.GetBytes("pdf bytes"));
        string hash = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
        string artifactRoot = Path.Combine(_root, "published");
        Directory.CreateDirectory(artifactRoot);
        await WriteArtifactAsync(
            artifactRoot,
            hash,
            "Hybrid",
            "qwen3-vl:8b",
            "qwen3-vl:8b");
        PublishedExtractionContentReader reader = new(
            artifactRoot,
            "qwen3-vl:8b",
            "Hybrid");

        ContentChunk chunk = Assert.Single(
            await reader.ReadAsync(
                new ContentSourceDescriptor
                {
                    OwnerId = Guid.NewGuid(),
                    Kind = ContentSourceKind.Pdf,
                    Path = sourcePath,
                },
                CancellationToken.None));

        Assert.Equal("Extracted text", chunk.Text);
        Assert.Equal(ExpectedProvenance, chunk.Provenance);
    }

    [Fact]
    public async Task RejectsArtifactWhosePayloadIdentityDoesNotMatch()
    {
        Directory.CreateDirectory(_root);
        string sourcePath = Path.Combine(_root, "book.pdf");
        await File.WriteAllBytesAsync(
            sourcePath,
            Encoding.UTF8.GetBytes("pdf bytes"));
        string hash = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
        string artifactRoot = Path.Combine(_root, "published");
        Directory.CreateDirectory(artifactRoot);
        await WriteArtifactAsync(
            artifactRoot,
            hash,
            "Hybrid",
            "qwen3-vl:4b",
            payloadModel: "qwen3-vl:8b");
        PublishedExtractionContentReader reader = new(
            artifactRoot,
            "qwen3-vl:4b",
            "Hybrid");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reader.ReadAsync(
                new ContentSourceDescriptor
                {
                    OwnerId = Guid.NewGuid(),
                    Kind = ContentSourceKind.Pdf,
                    Path = sourcePath,
                },
                CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task WriteArtifactAsync(
        string artifactRoot,
        string sourceHash,
        string mode,
        string fileModel,
        string payloadModel)
    {
        string modelHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(fileModel)))[..16];
        string path = Path.Combine(
            artifactRoot,
            $"{sourceHash}.{mode.ToLowerInvariant()}.{modelHash}.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                manifestItemId = "book",
                sourceSha256 = sourceHash,
                extractionProfile = mode,
                model = payloadModel,
                tokenizerIdentity =
                    ApplicationChunkTokenizer.IdentityHash,
                chunkingIdentity = HeadingAwareChunker.Identity,
                chunks = new[]
                {
                    new
                    {
                        ordinal = 0,
                        text = "Extracted text",
                        heading = "Heading",
                        sourceStart = 0,
                        sourceEnd = 14,
                        pageNumber = 1,
                        tokenCount = 3,
                        provenance = ExpectedProvenance,
                    },
                },
            }));
    }
}
