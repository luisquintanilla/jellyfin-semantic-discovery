using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Manifest;
using Jellyfin.SemanticDiscovery.Extraction.Pdf;
using Jellyfin.SemanticDiscovery.Extraction.Validation;

namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

public sealed class SourceValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task ValidatesContainedPdfHashSizeAndPageCount()
    {
        using TestDirectory directory = new();
        byte[] content = "%PDF synthetic"u8.ToArray();
        string sourcePath = directory.GetPath("document.pdf");
        await File.WriteAllBytesAsync(sourcePath, content);
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        string manifestPath = await WriteManifestAsync(
            directory,
            new("item", "document.pdf", hash, content.Length, 2));
        ExtractionSourceValidator validator = CreateValidator(
            manifestPath,
            pageCount: 2);

        ValidatedExtractionSource source = await validator.ValidateAsync(
            "item",
            hash,
            ExtractionProfile.Hybrid,
            CancellationToken.None);

        Assert.Equal(sourcePath, source.SourcePath);
        Assert.Equal(hash, source.SourceSha256);
        Assert.Equal(2, source.PageCount);
    }

    [Theory]
    [InlineData("..\\outside.pdf", "escapes")]
    [InlineData("document.txt", "Only .pdf")]
    public async Task RejectsInvalidManifestPaths(
        string sourcePath,
        string expectedMessage)
    {
        using TestDirectory directory = new();
        string hash = new('a', 64);
        string manifestPath = await WriteManifestAsync(
            directory,
            new("item", sourcePath, hash, 1, 1));
        ExtractionSourceValidator validator = CreateValidator(
            manifestPath,
            pageCount: 1);

        ExtractionValidationException exception =
            await Assert.ThrowsAsync<ExtractionValidationException>(
                async () => await validator.ValidateAsync(
                    "item",
                    hash,
                    ExtractionProfile.Text,
                    CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task RejectsHashSizeAndPageLimitMismatches()
    {
        using TestDirectory directory = new();
        byte[] content = "%PDF synthetic"u8.ToArray();
        string sourcePath = directory.GetPath("document.pdf");
        await File.WriteAllBytesAsync(sourcePath, content);
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        string manifestPath = await WriteManifestAsync(
            directory,
            new("item", "document.pdf", hash, content.Length, 3),
            "correct-manifest.json");
        ExtractionSourceValidator wrongHash = CreateValidator(
            manifestPath,
            pageCount: 3);
        ExtractionSourceValidator wrongSize = CreateValidator(
            await WriteManifestAsync(
                directory,
                new("item", "document.pdf", hash, content.Length + 1, 3),
                "wrong-size-manifest.json"),
            pageCount: 3);
        ExtractionSourceValidator pageLimit = CreateValidator(
            manifestPath,
            pageCount: 3,
            maximumPages: 2);

        await Assert.ThrowsAsync<ExtractionValidationException>(
            async () => await wrongHash.ValidateAsync(
                "item",
                new string('b', 64),
                ExtractionProfile.Text,
                CancellationToken.None));
        await Assert.ThrowsAsync<ExtractionValidationException>(
            async () => await wrongSize.ValidateAsync(
                "item",
                hash,
                ExtractionProfile.Text,
                CancellationToken.None));
        await Assert.ThrowsAsync<ExtractionValidationException>(
            async () => await pageLimit.ValidateAsync(
                "item",
                hash,
                ExtractionProfile.Text,
                CancellationToken.None));
    }

    [Fact]
    public async Task RejectsActualSourceHashAndManifestPageCountMismatches()
    {
        using TestDirectory directory = new();
        byte[] content = "%PDF synthetic"u8.ToArray();
        string sourcePath = directory.GetPath("document.pdf");
        await File.WriteAllBytesAsync(sourcePath, content);
        string wrongHash = new('b', 64);
        string hashManifest = await WriteManifestAsync(
            directory,
            new(
                "item",
                "document.pdf",
                wrongHash,
                content.Length,
                2),
            "wrong-hash-manifest.json");
        string actualHash = Convert.ToHexStringLower(
            SHA256.HashData(content));
        string pageManifest = await WriteManifestAsync(
            directory,
            new(
                "item",
                "document.pdf",
                actualHash,
                content.Length,
                3),
            "wrong-pages-manifest.json");

        await Assert.ThrowsAsync<ExtractionValidationException>(
            async () => await CreateValidator(
                hashManifest,
                pageCount: 2).ValidateAsync(
                    "item",
                    wrongHash,
                    ExtractionProfile.Text,
                    CancellationToken.None));
        await Assert.ThrowsAsync<ExtractionValidationException>(
            async () => await CreateValidator(
                pageManifest,
                pageCount: 2).ValidateAsync(
                    "item",
                    actualHash,
                    ExtractionProfile.Text,
                    CancellationToken.None));
    }

    private static ExtractionSourceValidator CreateValidator(
        string manifestPath,
        int pageCount,
        int maximumPages = 10) =>
        new(
            new JsonExtractionManifestCatalog(manifestPath),
            new FakePdfInspector(pageCount),
            new ExtractionOptions
            {
                MaximumPages = maximumPages,
                MaximumSourceBytes = 1024,
            });

    private static async ValueTask<string> WriteManifestAsync(
        TestDirectory directory,
        ExtractionManifestItem item,
        string fileName = "manifest.json")
    {
        string path = directory.GetPath(fileName);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new ExtractionManifest(1, [item]),
                JsonOptions));
        return path;
    }

    private sealed class FakePdfInspector(int pageCount) :
        IPdfDocumentInspector
    {
        public ValueTask<int> GetPageCountAsync(
            string sourcePath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(pageCount);
    }
}
