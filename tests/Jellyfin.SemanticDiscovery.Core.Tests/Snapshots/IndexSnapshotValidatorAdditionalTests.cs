using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Snapshots;

/// <summary>
/// Additional <see cref="IndexSnapshotValidator"/> regression coverage
/// (duplicate ids, blank ids/hashes, non-finite vectors, dangling content
/// owners, and blank identity values), kept in a separate file/fixture from
/// <see cref="IndexSnapshotValidatorTests"/> so both can evolve
/// independently.
/// </summary>
public sealed class IndexSnapshotValidatorAdditionalTests
{
    [Fact]
    public void ValidateAcceptsAMinimalValidSnapshot()
    {
        IndexSnapshotValidator.Validate(CreateSnapshot());
    }

    [Fact]
    public void ValidateRejectsDuplicateMetadataIds()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        PersistedMetadataVector duplicate = snapshot.Metadata[0] with
        {
            Record = snapshot.Metadata[0].Record with { Title = "Duplicate Title" },
        };

        Assert.Throws<InvalidDataException>(() => IndexSnapshotValidator.Validate(
            snapshot with { Metadata = [snapshot.Metadata[0], duplicate] }));
    }

    [Fact]
    public void ValidateRejectsDuplicateContentIds()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        PersistedContentVector duplicate = snapshot.Content[0] with { };

        Assert.Throws<InvalidDataException>(() => IndexSnapshotValidator.Validate(
            snapshot with { Content = [snapshot.Content[0], duplicate] }));
    }

    [Fact]
    public void ValidateRejectsEmptyMetadataId()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        MetadataRecord record = snapshot.Metadata[0].Record with { Id = Guid.Empty };

        Assert.Throws<InvalidDataException>(() => IndexSnapshotValidator.Validate(
            snapshot with
            {
                Metadata = [snapshot.Metadata[0] with { Record = record }],
                Content = [],
            }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRejectsBlankContentId(string blankId)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        ContentChunk chunk = snapshot.Content[0].Chunk with { Id = blankId };

        Assert.Throws<InvalidDataException>(() => IndexSnapshotValidator.Validate(
            snapshot with { Content = [snapshot.Content[0] with { Chunk = chunk }] }));
    }

    [Fact]
    public void ValidateRejectsMetadataVectorContainingANonFiniteValue()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        float[] vector = UnitVector();
        vector[5] = float.NaN;

        Assert.Throws<InvalidDataException>(() => IndexSnapshotValidator.Validate(
            snapshot with
            {
                Metadata = [snapshot.Metadata[0] with { Vector = vector }],
            }));
    }

    [Fact]
    public void ValidateRejectsContentVectorContainingANonFiniteValue()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        float[] vector = UnitVector();
        vector[7] = float.PositiveInfinity;

        Assert.Throws<InvalidDataException>(() => IndexSnapshotValidator.Validate(
            snapshot with
            {
                Content = [snapshot.Content[0] with { Vector = vector }],
            }));
    }

    [Fact]
    public void ValidateRejectsContentReferencingAnUnknownOwner()
    {
        IndexSnapshot snapshot = CreateSnapshot();
        ContentChunk dangling = TestData.Chunk(Guid.NewGuid(), text: "orphan cue text");

        Assert.Throws<InvalidDataException>(() => IndexSnapshotValidator.Validate(
            snapshot with { Content = [Persist(dangling)] }));
    }

    [Theory]
    [InlineData("modelRevision")]
    [InlineData("tokenizerRevision")]
    [InlineData("projectionVersion")]
    public void ValidateRejectsBlankIdentityRevisionValues(string field)
    {
        IndexSnapshot snapshot = CreateSnapshot();
        IndexIdentity identity = field switch
        {
            "modelRevision" => snapshot.Identity with { ModelRevision = "   " },
            "tokenizerRevision" => snapshot.Identity with { TokenizerRevision = "   " },
            "projectionVersion" => snapshot.Identity with { ProjectionVersion = "   " },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Throws<InvalidDataException>(
            () => IndexSnapshotValidator.Validate(snapshot with { Identity = identity }));
    }

    [Fact]
    public void ValidateRejectsNullSnapshot()
    {
        Assert.Throws<ArgumentNullException>(
            static () => IndexSnapshotValidator.Validate(null!));
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

    private static float[] UnitVector()
    {
        float[] vector = new float[SemanticConstants.EmbeddingDimensions];
        vector[0] = 1F;
        return vector;
    }
}
