using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class SemanticSourceIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"source-integrity-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ValidateIfPresentChecksExactBytesFromTheReaderBuffer()
    {
        Directory.CreateDirectory(_root);
        byte[] exact = "exact source bytes"u8.ToArray();
        string sourcePath = Path.Combine(_root, "book.pdf");
        File.WriteAllBytes(
            Path.Combine(_root, "semantic-discovery.metadata.json"),
            JsonSerializer.SerializeToUtf8Bytes(new SemanticMetadataSidecar
            {
                SchemaVersion = 2,
                ProjectionHashContract =
                    Jellyfin.SemanticDiscovery.Core.Projection
                        .MetadataProjectionIdentity.ContractId,
                Kind = MediaKind.Book,
                ItemType = "Book",
                MediaType = "Book",
                Title = "Book",
                ProductionYear = 2024,
                Overview = "Overview",
                Genres = [],
                Tags = [],
                People = [],
                Studios = [],
                ProjectedMetadataSha256 = new string('0', 64),
                ContentSources =
                [
                    new SemanticSourceLock
                    {
                        FileName = "book.pdf",
                        Kind = ContentSourceKind.Pdf,
                        Size = exact.Length,
                        Sha256 = Convert.ToHexStringLower(
                            SHA256.HashData(exact)),
                    },
                ],
            }));

        SemanticSourceIntegrity.ValidateIfPresent(
            sourcePath,
            ContentSourceKind.Pdf,
            exact);
        exact[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(
            () => SemanticSourceIntegrity.ValidateIfPresent(
                sourcePath,
                ContentSourceKind.Pdf,
                exact));
    }

    [Fact]
    public void ValidateIfPresentDoesNothingWithoutShowcaseSidecar()
    {
        Directory.CreateDirectory(_root);

        SemanticSourceIntegrity.ValidateIfPresent(
            Path.Combine(_root, "synthetic.srt"),
            ContentSourceKind.Subtitle,
            "synthetic"u8);
    }
}
