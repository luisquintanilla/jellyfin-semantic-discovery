using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Retrieval;

public sealed class SemanticDiscoveryServiceTests
{
    private static (SemanticDiscoveryService Service, RecordingEmbeddingGenerator Generator, RecordingVectorSearchIndex Index, RecordingAccessPolicy AccessPolicy) CreateService(
        SearchScoreCalibration? calibration = null)
    {
        RecordingEmbeddingGenerator generator = new();
        RecordingVectorSearchIndex index = new();
        RecordingAccessPolicy accessPolicy = new();
        SemanticDiscoveryService service = new(
            generator,
            index,
            accessPolicy,
            calibration ?? new SearchScoreCalibration());
        return (service, generator, index, accessPolicy);
    }

    [Fact]
    public async Task SearchAsyncGeneratesExactlyOneQueryEmbedding()
    {
        (SemanticDiscoveryService service, RecordingEmbeddingGenerator generator, _, _) = CreateService();

        await service.SearchAsync("some query", new SearchScope(), limit: 5);

        Assert.Single(generator.Calls);
        Assert.Equal(["some query"], generator.Calls[0]);
    }

    [Fact]
    public async Task SearchAsyncSearchesBothMetadataAndContentStores()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();

        await service.SearchAsync("query", new SearchScope(), limit: 5);

        Assert.Single(index.MetadataSearchCalls);
        Assert.Single(index.ContentSearchCalls);
    }

    [Theory]
    [InlineData(5, 32)]
    [InlineData(10, 60)]
    [InlineData(1, 32)]
    public async Task SearchAsyncRequestsOverFetchCountBasedOnCalibration(int limit, int expectedOverFetch)
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();

        await service.SearchAsync("query", new SearchScope(), limit);

        Assert.Equal(expectedOverFetch, index.MetadataSearchCalls[0].Count);
        Assert.Equal(expectedOverFetch, index.ContentSearchCalls[0].Count);
    }

    [Fact]
    public async Task SearchAsyncFusesMetadataAndContentMatchesForTheSameItemIntoOneResult()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        MetadataRecord record = TestData.Movie(title: "Shared Item");
        ContentChunk chunk = TestData.Chunk(record.Id, text: "chunk");
        index.MetadataResults = [new MetadataMatch(record, 0.9F, 0)];
        index.ContentResults = [new ContentMatch(chunk, 0.9F, 0)];

        IReadOnlyList<SemanticSearchResult> results =
            await service.SearchAsync("query", new SearchScope(), limit: 5);

        Assert.Single(results);
        Assert.Equal(record.Id, results[0].ItemId);
    }

    [Fact]
    public async Task SearchAsyncResolvesOwnerMetadataForContentOnlyMatchesViaGetMetadataAsync()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        MetadataRecord owner = TestData.Movie(title: "Content Owner");
        ContentChunk chunk = TestData.Chunk(owner.Id, text: "chunk");
        index.MetadataResults = [];
        index.ContentResults = [new ContentMatch(chunk, 0.9F, 0)];
        index.Records[owner.Id] = owner;

        IReadOnlyList<SemanticSearchResult> results =
            await service.SearchAsync("query", new SearchScope(), limit: 5);

        Assert.Contains(owner.Id, index.GetMetadataCalls);
        Assert.Equal(owner.Id, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task SearchAsyncAppliesAllStructuralFiltersBeforeExactlyOneAccessPolicyCall()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, RecordingAccessPolicy access) =
            CreateService();
        Guid library = Guid.NewGuid();
        Guid parent = Guid.NewGuid();
        MetadataRecord eligible = TestData.Movie(
            itemType: "Movie", mediaType: "Video", libraryId: library, parentId: parent);
        MetadataRecord excludedByKind = TestData.Book() with
        {
            LibraryId = library,
            ParentId = parent,
        };
        MetadataRecord excludedByItemType = TestData.Movie(
            itemType: "Trailer", mediaType: "Video", libraryId: library, parentId: parent);
        MetadataRecord excludedByMediaType = TestData.Movie(
            itemType: "Movie", mediaType: "Audio", libraryId: library, parentId: parent);
        MetadataRecord excludedByLibrary = TestData.Movie(
            itemType: "Movie", mediaType: "Video", libraryId: Guid.NewGuid(), parentId: parent);
        MetadataRecord excludedByParent = TestData.Movie(
            itemType: "Movie", mediaType: "Video", libraryId: library, parentId: Guid.NewGuid());
        MetadataRecord[] all =
        [
            eligible, excludedByKind, excludedByItemType,
            excludedByMediaType, excludedByLibrary, excludedByParent,
        ];
        index.MetadataResults = all
            .Select((record, i) => new MetadataMatch(record, 0.9F, i))
            .ToArray();
        SearchScope scope = new()
        {
            Kinds = new HashSet<MediaKind> { MediaKind.Movie },
            IncludeItemTypes = new HashSet<string>(StringComparer.Ordinal) { "Movie" },
            MediaTypes = new HashSet<string>(StringComparer.Ordinal) { "Video" },
            LibraryId = library,
            ParentId = parent,
        };

        await service.SearchAsync("query", scope, limit: 10);

        Assert.Single(access.CandidateCalls);
        Assert.Equal([eligible.Id], access.CandidateCalls[0].Select(static r => r.Id));
    }

    [Fact]
    public async Task SearchAsyncExcludesItemsDeniedByAccessPolicy()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, RecordingAccessPolicy access) =
            CreateService();
        MetadataRecord allowed = TestData.Movie(title: "Allowed");
        MetadataRecord denied = TestData.Movie(title: "Denied");
        index.MetadataResults =
        [
            new MetadataMatch(allowed, 0.9F, 0),
            new MetadataMatch(denied, 0.9F, 1),
        ];
        access.Resolve = _ => new HashSet<Guid> { allowed.Id };

        IReadOnlyList<SemanticSearchResult> results =
            await service.SearchAsync("query", new SearchScope(), limit: 10);

        Assert.Equal(allowed.Id, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task SearchAsyncAppliesAccessFilterBeforeTopKSoDeniedCrowdCannotHideEligibleResult()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, RecordingAccessPolicy access) =
            CreateService();
        MetadataRecord eligible = TestData.Movie(title: "Eligible");
        MetadataRecord[] denied = Enumerable.Range(0, 40)
            .Select(number => TestData.Movie(title: $"Denied {number}"))
            .ToArray();
        index.MetadataResults = denied
            .Select((record, rank) => new MetadataMatch(record, 0.99F, rank))
            .Append(new MetadataMatch(eligible, 0.70F, denied.Length))
            .ToArray();
        access.Resolve = _ => new HashSet<Guid> { eligible.Id };

        IReadOnlyList<SemanticSearchResult> results =
            await service.SearchAsync("eligible meaning", new SearchScope(), limit: 1);

        Assert.Equal(32, index.MetadataSearchCalls[0].Count);
        Assert.Equal([eligible.Id], index.MetadataSearchCalls[0].AllowedIds);
        Assert.Equal(eligible.Id, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task SearchAsyncReturnsAtMostLimitResultsInFusedScoreOrder()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        MetadataRecord low = TestData.Movie(title: "Low");
        MetadataRecord medium = TestData.Movie(title: "Medium");
        MetadataRecord high = TestData.Movie(title: "High");
        index.MetadataResults =
        [
            new MetadataMatch(low, 0.40F, 0),
            new MetadataMatch(medium, 0.60F, 1),
            new MetadataMatch(high, 0.90F, 2),
        ];

        IReadOnlyList<SemanticSearchResult> results =
            await service.SearchAsync("query", new SearchScope(), limit: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal(high.Id, results[0].ItemId);
        Assert.Equal(medium.Id, results[1].ItemId);
    }

    [Fact]
    public async Task SearchAsyncReturnsEmptyWhenNoMatchesClearEitherFloor()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        SearchScoreCalibration calibration = new();
        MetadataRecord atFloor = TestData.Movie(title: "At Floor");
        index.MetadataResults = [new MetadataMatch(atFloor, calibration.MetadataFloor, 0)];

        IReadOnlyList<SemanticSearchResult> results =
            await service.SearchAsync("negative query", new SearchScope(), limit: 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSimilarAsyncReturnsEmptyWhenSourceItemIsMissing()
    {
        (SemanticDiscoveryService service, _, _, _) = CreateService();

        IReadOnlyList<SimilarItemResult> results = await service.GetSimilarAsync(
            Guid.NewGuid(), new SearchScope(), new HashSet<Guid>(), limit: 5);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSimilarAsyncReturnsEmptyWhenSourceItemHasNoMetadataVector()
    {
        // Regression test for the InMemorySemanticVectorIndex fix: a missing
        // vector must surface as KeyNotFoundException, not a 0-length query
        // vector reaching SearchMetadataAsync (which would instead throw an
        // unrelated ArgumentException about the 384-dimension contract).
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        MetadataRecord source = TestData.Movie(title: "No Vector");
        index.Records[source.Id] = source;

        IReadOnlyList<SimilarItemResult> results = await service.GetSimilarAsync(
            source.Id, new SearchScope(), new HashSet<Guid>(), limit: 5);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSimilarAsyncExcludesSourceItemAndExplicitExclusions()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        MetadataRecord source = TestData.Movie(title: "Source");
        MetadataRecord excluded = TestData.Movie(title: "Excluded");
        MetadataRecord candidate = TestData.Movie(title: "Candidate");
        index.Records[source.Id] = source;
        index.Vectors[source.Id] = new float[SemanticConstants.EmbeddingDimensions];
        index.MetadataResults =
        [
            new MetadataMatch(source, 1.0F, 0),
            new MetadataMatch(excluded, 0.9F, 1),
            new MetadataMatch(candidate, 0.8F, 2),
        ];

        IReadOnlyList<SimilarItemResult> results = await service.GetSimilarAsync(
            source.Id, new SearchScope(), new HashSet<Guid> { excluded.Id }, limit: 5);

        Assert.Equal(candidate.Id, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task GetSimilarAsyncOnlyReturnsCandidatesWithTheSameMediaKind()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        MetadataRecord source = TestData.Movie(title: "Source");
        MetadataRecord sameKind = TestData.Movie(title: "Same Kind");
        MetadataRecord differentKind = TestData.Book(title: "Different Kind");
        index.Records[source.Id] = source;
        index.Vectors[source.Id] = new float[SemanticConstants.EmbeddingDimensions];
        index.MetadataResults =
        [
            new MetadataMatch(sameKind, 0.9F, 0),
            new MetadataMatch(differentKind, 0.9F, 1),
        ];

        IReadOnlyList<SimilarItemResult> results = await service.GetSimilarAsync(
            source.Id, new SearchScope(), new HashSet<Guid>(), limit: 5);

        Assert.Equal(sameKind.Id, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task GetSimilarAsyncExcludesCandidatesDeniedByAccessPolicy()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, RecordingAccessPolicy access) =
            CreateService();
        MetadataRecord source = TestData.Movie(title: "Source");
        MetadataRecord allowed = TestData.Movie(title: "Allowed");
        MetadataRecord denied = TestData.Movie(title: "Denied");
        index.Records[source.Id] = source;
        index.Vectors[source.Id] = new float[SemanticConstants.EmbeddingDimensions];
        index.MetadataResults =
        [
            new MetadataMatch(allowed, 0.9F, 0),
            new MetadataMatch(denied, 0.8F, 1),
        ];
        access.Resolve = _ => new HashSet<Guid> { allowed.Id };

        IReadOnlyList<SimilarItemResult> results = await service.GetSimilarAsync(
            source.Id, new SearchScope(), new HashSet<Guid>(), limit: 5);

        Assert.Equal(allowed.Id, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task GetSimilarAsyncOrdersBySimilarityDescendingThenTitleThenId()
    {
        (SemanticDiscoveryService service, _, RecordingVectorSearchIndex index, _) = CreateService();
        Guid lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        MetadataRecord source = TestData.Movie(title: "Source");
        MetadataRecord beta = TestData.Movie(title: "Beta");
        MetadataRecord alpha = TestData.Movie(title: "Alpha");
        MetadataRecord sameTitleLower = TestData.Movie(id: lowerId, title: "Same");
        MetadataRecord sameTitleHigher = TestData.Movie(id: higherId, title: "same");
        index.Records[source.Id] = source;
        index.Vectors[source.Id] = new float[SemanticConstants.EmbeddingDimensions];
        index.MetadataResults =
        [
            new MetadataMatch(beta, 0.7F, 0),
            new MetadataMatch(alpha, 0.7F, 1),
            new MetadataMatch(sameTitleLower, 0.5001F, 2),
            new MetadataMatch(sameTitleHigher, 0.5001F, 3),
        ];

        IReadOnlyList<SimilarItemResult> results = await service.GetSimilarAsync(
            source.Id, new SearchScope(), new HashSet<Guid>(), limit: 10);

        Assert.Equal(
            [alpha.Id, beta.Id, lowerId, higherId],
            results.Select(static result => result.ItemId));
    }
}
