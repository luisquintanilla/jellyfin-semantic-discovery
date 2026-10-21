using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Indexing;

public sealed class SubtitleContentReaderTests
{
    [Fact]
    public async Task ReadAsyncMapsParsedCuesThroughMediContracts()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"subtitle-medi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "movie.en.srt");
            await File.WriteAllTextAsync(
                path,
                """
                1
                00:00:01,000 --> 00:00:02,500
                Hello world

                2
                00:00:03,000 --> 00:00:04,000
                Second cue
                """);
            Guid ownerId = Guid.NewGuid();

            ContentChunk chunk = Assert.Single(
                await new SubtitleContentReader(new WordCountTokenCounter())
                    .ReadAsync(
                        new ContentSourceDescriptor
                        {
                            OwnerId = ownerId,
                            Kind = ContentSourceKind.Subtitle,
                            Path = path,
                            Language = "en",
                        },
                        CancellationToken.None));

            Assert.Equal(ownerId, chunk.OwnerId);
            Assert.Equal(path, chunk.SourcePath);
            Assert.Equal(ContentSourceKind.Subtitle, chunk.SourceKind);
            Assert.Equal("Hello world Second cue", chunk.Text);
            Assert.Equal("en", chunk.Language);
            Assert.Equal(TimeSpan.FromSeconds(1).Ticks, chunk.StartTicks);
            Assert.Equal(TimeSpan.FromSeconds(4).Ticks, chunk.EndTicks);
            Assert.Equal(0, chunk.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsyncRejectsMutatedSubtitleAgainstShowcaseSidecar()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"subtitle-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] expected =
                "1\n00:00:00,000 --> 00:00:01,000\nExpected\n"u8.ToArray();
            string path = Path.Combine(directory, "movie.en.srt");
            await File.WriteAllTextAsync(
                path,
                "1\n00:00:00,000 --> 00:00:01,000\nMutated\n");
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "semantic-discovery.metadata.json"),
                JsonSerializer.SerializeToUtf8Bytes(new SemanticMetadataSidecar
                {
                    SchemaVersion = 2,
                    ProjectionHashContract =
                        Jellyfin.SemanticDiscovery.Core.Projection
                            .MetadataProjectionIdentity.ContractId,
                    Kind = MediaKind.Movie,
                    ItemType = "Movie",
                    MediaType = "Video",
                    Title = "Movie",
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
                            FileName = Path.GetFileName(path),
                            Kind = ContentSourceKind.Subtitle,
                            Size = expected.Length,
                            Sha256 = Convert.ToHexStringLower(
                                SHA256.HashData(expected)),
                        },
                    ],
                }));
            SubtitleContentReader reader = new(
                new WordCountTokenCounter());

            ContentIngestionException exception =
                await Assert.ThrowsAsync<ContentIngestionException>(
                    () => reader.ReadAsync(
                        new ContentSourceDescriptor
                        {
                            OwnerId = Guid.NewGuid(),
                            Kind = ContentSourceKind.Subtitle,
                            Path = path,
                            Language = "en",
                        },
                        CancellationToken.None).AsTask());

            Assert.False(exception.IsTransient);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsTransientReadFailureTreatsGenericIoAsTransient()
    {
        Assert.True(SubtitleContentReader.IsTransientReadFailure(
            new IOException("temporary read fault")));
    }

    [Fact]
    public void IsTransientReadFailureKeepsKnownSourceProblemsPermanent()
    {
        Assert.False(SubtitleContentReader.IsTransientReadFailure(
            new FileNotFoundException()));
        Assert.False(SubtitleContentReader.IsTransientReadFailure(
            new DirectoryNotFoundException()));
        Assert.False(SubtitleContentReader.IsTransientReadFailure(
            new InvalidDataException()));
        Assert.True(SubtitleContentReader.IsTransientReadFailure(
            new UnauthorizedAccessException()));
    }
}
