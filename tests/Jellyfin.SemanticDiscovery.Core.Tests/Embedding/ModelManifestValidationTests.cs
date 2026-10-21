using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Setup;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Embedding;

public sealed class ModelManifestValidationTests
{
    [Fact]
    public async Task CommittedManifestValidatesAgainstLiteralPinnedIdentity()
    {
        string manifestPath = FindRepositoryFile(
            Path.Combine("assets", "model-manifest.json"));

        ModelManifest manifest = await ModelInstaller.LoadManifestAsync(
            manifestPath,
            CancellationToken.None);

        manifest.Validate();
        Assert.Equal(
            "1110a243fdf4706b3f48f1d95db1a4f5529b4d41",
            manifest.Revision);
        Assert.Equal(
            "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452",
            Assert.Single(manifest.Files, file => file.Path == "model.onnx").Sha256);
        Assert.Equal(
            "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
            Assert.Single(manifest.Files, file => file.Path == "vocab.txt").Sha256);
        Assert.Equal(
            "b72552a2c29d6e86dc231d17dcd1acb94ef1b783d708d08f8439dd4cd66a2c70",
            manifest.Tokenizer.IdentitySha256);
        Assert.Equal(MiniLmTokenizerIdentity.Hash, manifest.Tokenizer.IdentitySha256);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("license-url")]
    [InlineData("source-path")]
    [InlineData("size")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    public async Task ValidateRejectsAnyManifestThatIsNotTheExactPinnedSet(
        string mutation)
    {
        ModelManifest manifest = await LoadCommittedAsync();
        ModelFile model = manifest.Files.Single(file => file.Path == "model.onnx");
        ModelManifest invalid = mutation switch
        {
            "repository" => manifest with { Repository = "other/model" },
            "license-url" => manifest with { LicenseUrl = "https://example.invalid" },
            "source-path" => manifest with
            {
                Files =
                [
                    model with { SourcePath = "other/model.onnx" },
                    manifest.Files.Single(file => file.Path == "vocab.txt"),
                ],
            },
            "size" => manifest with
            {
                Files =
                [
                    model with { Size = model.Size + 1 },
                    manifest.Files.Single(file => file.Path == "vocab.txt"),
                ],
            },
            "duplicate" => manifest with { Files = [model, model] },
            "extra" => manifest with
            {
                Files =
                [
                    .. manifest.Files,
                    model with { Path = "extra.bin" },
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.Throws<InvalidDataException>(invalid.Validate);
    }

    [Fact]
    public void ValidateDeclaredContentLengthRejectsMismatch()
    {
        Assert.Throws<InvalidDataException>(
            () => ModelInstaller.ValidateDeclaredContentLength(
                declaredLength: 6,
                expectedSize: 5,
                path: "model.onnx"));
    }

    [Fact]
    public async Task CopyBoundedAsyncStopsBeforeWritingOversizedResponse()
    {
        await using MemoryStream source = new(new byte[6]);
        await using MemoryStream destination = new();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ModelInstaller.CopyBoundedAsync(
                source,
                destination,
                expectedSize: 5,
                CancellationToken.None).AsTask());

        Assert.Equal(0, destination.Length);
        Assert.Equal(6, source.Position);
    }

    private static async Task<ModelManifest> LoadCommittedAsync() =>
        await ModelInstaller.LoadManifestAsync(
            FindRepositoryFile(Path.Combine("assets", "model-manifest.json")),
            CancellationToken.None);

    private static string FindRepositoryFile(string relativePath)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }
}
