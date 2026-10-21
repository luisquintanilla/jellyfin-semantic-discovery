using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Snapshots;

public sealed class IndexSnapshotValidatorTests
{
    [Fact]
    public void ValidateAcceptsValidCueAndDocumentChunks()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        MetadataRecord owner = snapshot.Metadata[0].Record;
        ContentChunk pdf = TestData.Chunk(
            owner.Id,
            "book.pdf",
            ContentSourceKind.Pdf,
            text: "document text");
        ContentChunk epub = TestData.Chunk(
            owner.Id,
            "book.epub",
            ContentSourceKind.Epub,
            ordinal: 1,
            text: "chapter text");
        snapshot = TestData.Seal(snapshot with
        {
            Content =
            [
                snapshot.Content[0],
                Persist(pdf),
                Persist(epub),
            ],
        });

        IndexSnapshotValidator.Validate(snapshot);
    }

    [Theory]
    [InlineData(ContentSourceKind.Subtitle, true, false)]
    [InlineData(ContentSourceKind.Subtitle, false, true)]
    [InlineData(ContentSourceKind.Transcript, true, false)]
    [InlineData(ContentSourceKind.Transcript, false, true)]
    [InlineData(ContentSourceKind.Pdf, true, false)]
    [InlineData(ContentSourceKind.Pdf, false, true)]
    [InlineData(ContentSourceKind.Epub, true, false)]
    [InlineData(ContentSourceKind.Epub, false, true)]
    public void ValidateRejectsOneSidedCueOrDocumentTicks(
        ContentSourceKind kind,
        bool hasStart,
        bool hasEnd)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        ContentChunk original = snapshot.Content[0].Chunk;
        ContentChunk chunk = Reidentify(original with
        {
            SourceKind = kind,
            StartTicks = hasStart ? 1 : null,
            EndTicks = hasEnd ? 2 : null,
        });

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with { Content = [Persist(chunk)] }));
    }

    [Fact]
    public void ValidateRejectsDocumentChunkWithBothCueTicks()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        ContentChunk chunk = Reidentify(snapshot.Content[0].Chunk with
        {
            SourceKind = ContentSourceKind.Pdf,
            StartTicks = 1,
            EndTicks = 2,
        });

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with { Content = [Persist(chunk)] }));
    }

    [Fact]
    public void ValidateRejectsTamperedChunkTextWithStaleHashAndKey()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        ContentChunk tampered = snapshot.Content[0].Chunk with
        {
            Text = "tampered text",
        };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with { Content = [Persist(tampered)] }));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("path")]
    [InlineData("kind")]
    [InlineData("ordinal")]
    public void ValidateRejectsTamperedChunkIdentityFieldsWithStaleKey(string field)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        ContentChunk chunk = snapshot.Content[0].Chunk;
        ContentChunk tampered = field switch
        {
            "owner" => chunk with { OwnerId = Guid.NewGuid() },
            "path" => chunk with { SourcePath = "different.srt" },
            "kind" => chunk with { SourceKind = ContentSourceKind.Transcript },
            "ordinal" => chunk with { Ordinal = chunk.Ordinal + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with { Content = [Persist(tampered)] }));
    }

    [Theory]
    [InlineData("library")]
    [InlineData("parent")]
    [InlineData("ancestor-empty")]
    [InlineData("ancestor-duplicate")]
    public void ValidateRejectsMalformedMetadataIdentifiers(string field)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        Guid duplicate = Guid.NewGuid();
        MetadataRecord record = field switch
        {
            "library" => snapshot.Metadata[0].Record with { LibraryId = Guid.Empty },
            "parent" => snapshot.Metadata[0].Record with { ParentId = Guid.Empty },
            "ancestor-empty" => snapshot.Metadata[0].Record with
            {
                AncestorIds = [Guid.Empty],
            },
            "ancestor-duplicate" => snapshot.Metadata[0].Record with
            {
                AncestorIds = [duplicate, duplicate],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with
                {
                    Metadata = [snapshot.Metadata[0] with { Record = record }],
                }));
    }

    [Theory]
    [InlineData("genres", true)]
    [InlineData("genres", false)]
    [InlineData("tags", true)]
    [InlineData("tags", false)]
    [InlineData("people", true)]
    [InlineData("people", false)]
    [InlineData("studios", true)]
    [InlineData("studios", false)]
    public void ValidateRejectsNullOrBlankMetadataArrayElements(
        string field,
        bool useNull)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        string[] values = useNull ? [null!] : [" "];
        MetadataRecord record = field switch
        {
            "genres" => snapshot.Metadata[0].Record with { Genres = values },
            "tags" => snapshot.Metadata[0].Record with { Tags = values },
            "people" => snapshot.Metadata[0].Record with { People = values },
            "studios" => snapshot.Metadata[0].Record with { Studios = values },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with
                {
                    Metadata = [snapshot.Metadata[0] with { Record = record }],
                }));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("tokenizer")]
    [InlineData("projection")]
    [InlineData("record-projection")]
    public void ValidateRejectsMalformedSha256Fields(string field)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        snapshot = field switch
        {
            "model" => snapshot with
            {
                Identity = snapshot.Identity with { ModelHash = "not-a-hash" },
            },
            "tokenizer" => snapshot with
            {
                Identity = snapshot.Identity with { TokenizerHash = "not-a-hash" },
            },
            "projection" => snapshot with
            {
                Identity = snapshot.Identity with { ProjectionHash = "not-a-hash" },
            },
            "record-projection" => snapshot with
            {
                Metadata =
                [
                    snapshot.Metadata[0] with { ProjectionHash = "not-a-hash" },
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(snapshot));
    }

    [Fact]
    public void ValidateRejectsMissingCreationTime()
    {
        IndexSnapshot snapshot = CreateSnapshot() with { CreatedUtc = default };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(snapshot));
    }

    [Theory]
    [InlineData("library")]
    [InlineData("parent")]
    public void ValidateRejectsMissingLibraryOrParentIdentifier(string field)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        MetadataRecord record = field == "library"
            ? snapshot.Metadata[0].Record with { LibraryId = null }
            : snapshot.Metadata[0].Record with { ParentId = null };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with
                {
                    Metadata = [snapshot.Metadata[0] with { Record = record }],
                }));
    }

    [Fact]
    public void ValidateRejectsDuplicateContentSourceOrdinalWithDifferentText()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        ContentChunk original = snapshot.Content[0].Chunk;
        ContentChunk conflicting = Reidentify(original with
        {
            Text = "different text in the same source slot",
        });

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(
                snapshot with
                {
                    Content =
                    [
                        snapshot.Content[0],
                        Persist(conflicting),
                    ],
                }));
    }

    private static IndexSnapshot CreateSnapshot()
    {
        MetadataRecord owner = TestData.Movie(title: "Owner");
        ContentChunk subtitle = TestData.Chunk(owner.Id, text: "cue text");
        return TestData.Seal(new IndexSnapshot
        {
            Identity = TestData.Identity(),
            CreatedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            Metadata =
            [
                new PersistedMetadataVector
                {
                    Record = owner,
                    ProjectionHash = ContentHash.Compute("owner projection"),
                    Vector = UnitVector(),
                },
            ],
            Content = [Persist(subtitle)],
        });
    }

    private static PersistedContentVector Persist(ContentChunk chunk) => new()
    {
        Chunk = chunk,
        Vector = UnitVector(),
    };

    private static ContentChunk Reidentify(ContentChunk chunk)
    {
        string contentHash = ContentHash.Compute(chunk.Text);
        return chunk with
        {
            ContentHash = contentHash,
            Id = ContentChunkIdentity.Compute(
                chunk.OwnerId,
                chunk.SourcePath,
                chunk.SourceKind,
                chunk.Ordinal,
                contentHash),
        };
    }

    private static float[] UnitVector()
    {
        float[] vector = new float[SemanticConstants.EmbeddingDimensions];
        vector[0] = 1F;
        return vector;
    }
}
