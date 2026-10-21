using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Retrieval;

public sealed class DeterministicScoreFusionTests
{
    private static readonly SearchScoreCalibration Calibration = new();

    private static DeterministicScoreFusion CreateFusion() => new(Calibration);

    private static float Calibrate(float similarity, float floor) =>
        similarity <= floor ? 0F : Math.Clamp((similarity - floor) / (1F - floor), 0F, 1F);

    [Fact]
    public void FuseScoresMetadataOnlyMatch()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord record = TestData.Movie(title: "Metadata Only Movie");
        float expected = Calibrate(0.7F, Calibration.MetadataFloor);

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [new MetadataMatch(record, Similarity: 0.7F, Rank: 0)],
            [],
            new Dictionary<Guid, MetadataRecord>());

        SemanticSearchResult result = Assert.Single(results);
        Assert.Equal(record.Id, result.ItemId);
        Assert.Equal(expected, result.Score, precision: 4);
    }

    [Fact]
    public void FuseScoresContentOnlyMatchWhenOwnerSuppliedButAbsentFromMetadataMatches()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord owner = TestData.Movie(title: "Content Only Movie");
        ContentChunk chunk = TestData.Chunk(owner.Id, text: "matching chunk");
        float expected = Calibrate(0.6F, Calibration.ContentFloor);

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [],
            [new ContentMatch(chunk, Similarity: 0.6F, Rank: 0)],
            new Dictionary<Guid, MetadataRecord> { [owner.Id] = owner });

        SemanticSearchResult result = Assert.Single(results);
        Assert.Equal(owner.Id, result.ItemId);
        Assert.Equal(expected, result.Score, precision: 4);
    }

    [Fact]
    public void FuseIgnoresContentMatchesWhoseOwnerIsNotSuppliedInOwnerMap()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        ContentChunk chunk = TestData.Chunk(Guid.NewGuid(), text: "orphan chunk");

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [],
            [new ContentMatch(chunk, Similarity: 0.9F, Rank: 0)],
            new Dictionary<Guid, MetadataRecord>());

        Assert.Empty(results);
    }

    [Fact]
    public void FuseCapsContentScoreToTopThreeChunksWithDiminishingWeightAndDedupesOwner()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord owner = TestData.Movie(title: "Multi Chunk Movie");
        float floor = Calibration.ContentFloor;
        float top1 = Calibrate(0.58F, floor);
        float top2 = Calibrate(0.54F, floor);
        float top3 = Calibrate(0.50F, floor);
        float excludedFourth = Calibrate(0.46F, floor);
        float expectedContentScore = Math.Clamp(top1 + ((top2 + top3) * 0.1F), 0F, 1F);
        float wouldBeScoreIfFourthIncluded =
            Math.Clamp(top1 + ((top2 + top3 + excludedFourth) * 0.1F), 0F, 1F);

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [],
            [
                new ContentMatch(TestData.Chunk(owner.Id, ordinal: 0, id: "c0", text: "c0"), 0.58F, 0),
                new ContentMatch(TestData.Chunk(owner.Id, ordinal: 1, id: "c1", text: "c1"), 0.54F, 1),
                new ContentMatch(TestData.Chunk(owner.Id, ordinal: 2, id: "c2", text: "c2"), 0.50F, 2),
                new ContentMatch(TestData.Chunk(owner.Id, ordinal: 3, id: "c3", text: "c3"), 0.46F, 3),
            ],
            new Dictionary<Guid, MetadataRecord> { [owner.Id] = owner });

        // Exactly one result per owner regardless of how many of its chunks
        // matched (owner dedupe), and the fourth (lowest) chunk score must
        // not have been included in the weighted sum.
        SemanticSearchResult result = Assert.Single(results);
        Assert.NotEqual(wouldBeScoreIfFourthIncluded, expectedContentScore);
        Assert.Equal(expectedContentScore, result.Score, precision: 5);
    }

    [Fact]
    public void FuseDropsMatchesAtOrBelowMetadataAndContentFloors()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord metadataAtFloor = TestData.Movie(title: "At Metadata Floor");
        MetadataRecord contentOwnerAtFloor = TestData.Movie(title: "At Content Floor");
        ContentChunk chunkAtFloor = TestData.Chunk(contentOwnerAtFloor.Id, text: "at floor");

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [new MetadataMatch(metadataAtFloor, Calibration.MetadataFloor, 0)],
            [new ContentMatch(chunkAtFloor, Calibration.ContentFloor, 0)],
            new Dictionary<Guid, MetadataRecord> { [contentOwnerAtFloor.Id] = contentOwnerAtFloor });

        Assert.Empty(results);
    }

    [Fact]
    public void FuseIncludesMatchesStrictlyAboveTheirFloor()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord record = TestData.Movie(title: "Above Floor");

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [new MetadataMatch(record, Calibration.MetadataFloor + 0.01F, 0)],
            [],
            new Dictionary<Guid, MetadataRecord>());

        Assert.True(Assert.Single(results).Score > 0F);
    }

    [Fact]
    public void FuseLinearlyRescalesSimilarityAboveFloor()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord recordLow = TestData.Movie(title: "Low");
        MetadataRecord recordHigh = TestData.Movie(title: "High");

        SemanticSearchResult resultLow = Assert.Single(fusion.Fuse(
            "q",
            [new MetadataMatch(recordLow, 0.44F, 0)],
            [],
            new Dictionary<Guid, MetadataRecord>()));
        SemanticSearchResult resultHigh = Assert.Single(fusion.Fuse(
            "q",
            [new MetadataMatch(recordHigh, 0.54F, 0)],
            [],
            new Dictionary<Guid, MetadataRecord>()));

        float floor = Calibration.MetadataFloor;
        float expectedLow = (0.44F - floor) / (1F - floor);
        float expectedHigh = (0.54F - floor) / (1F - floor);
        Assert.Equal(expectedLow, resultLow.Score, precision: 4);
        Assert.Equal(expectedHigh, resultHigh.Score, precision: 4);
        // Distance-above-floor doubles from 0.10 to 0.20, so the calibrated
        // score must double too, confirming the mapping is linear rather
        // than merely monotonic.
        Assert.Equal(2F, resultHigh.Score / resultLow.Score, precision: 2);
    }

    [Fact]
    public void FuseOrdersResultsByScoreDescendingThenTitleThenId()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        Guid lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        MetadataRecord beta = TestData.Movie(id: Guid.NewGuid(), title: "Beta");
        MetadataRecord alpha = TestData.Movie(id: Guid.NewGuid(), title: "Alpha");
        MetadataRecord sameTitleLowerId = TestData.Movie(id: lowerId, title: "Same");
        MetadataRecord sameTitleHigherId = TestData.Movie(id: higherId, title: "same");

        // beta/alpha share one similarity (tie broken by title); the
        // "Same"/"same" pair shares both score and normalized title (tie
        // broken by item id).
        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [
                new MetadataMatch(beta, 0.7F, 0),
                new MetadataMatch(alpha, 0.7F, 1),
                new MetadataMatch(sameTitleLowerId, 0.5F, 2),
                new MetadataMatch(sameTitleHigherId, 0.5F, 3),
            ],
            [],
            new Dictionary<Guid, MetadataRecord>());

        Assert.Equal(
            [alpha.Id, beta.Id, lowerId, higherId],
            results.Select(static result => result.ItemId));
    }

    [Fact]
    public void FuseAssignsExactTitleScoreWhenNormalizedTitleMatchesQueryRegardlessOfSimilarity()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord record = TestData.Movie(title: "  the   MATRIX  ");

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "The Matrix",
            [new MetadataMatch(record, Calibration.MetadataFloor + 0.001F, 0)],
            [],
            new Dictionary<Guid, MetadataRecord>());

        Assert.Equal(Calibration.ExactTitleScore, Assert.Single(results).Score);
    }

    [Fact]
    public void FuseCapsNonExactMatchesAtSemanticCeilingStrictlyBelowExactTitleScore()
    {
        DeterministicScoreFusion fusion = CreateFusion();
        MetadataRecord record = TestData.Movie(title: "Completely Different Title");

        IReadOnlyList<SemanticSearchResult> results = fusion.Fuse(
            "unrelated query",
            [new MetadataMatch(record, 1.0F, 0)],
            [],
            new Dictionary<Guid, MetadataRecord>());

        SemanticSearchResult result = Assert.Single(results);
        Assert.Equal(Calibration.SemanticScoreCeiling, result.Score);
        Assert.True(result.Score < Calibration.ExactTitleScore);
    }
}
