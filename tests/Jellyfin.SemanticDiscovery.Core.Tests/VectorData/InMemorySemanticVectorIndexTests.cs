using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Jellyfin.SemanticDiscovery.Core.VectorData;
using Microsoft.Extensions.VectorData;

namespace Jellyfin.SemanticDiscovery.Core.Tests.VectorData;

public sealed class InMemorySemanticVectorIndexTests
{
    [Fact]
    public async Task EmptyIndexSearchesReturnEmpty()
    {
        IVectorSearchReadSession session =
            new InMemorySemanticVectorIndex().OpenReadSession();

        Assert.Empty(await session.SearchMetadataAsync(
            BasisVector(0),
            5,
            new HashSet<Guid>(),
            CancellationToken.None));
        Assert.Empty(await session.SearchContentAsync(
            BasisVector(0),
            5,
            new HashSet<Guid>(),
            CancellationToken.None));
        Assert.Empty(await session.GetMetadataRecordsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(nameof(MetadataVectorDocument))]
    [InlineData(nameof(ContentVectorDocument))]
    public void VectorAttributesDeclare384DimensionsAndCosineSimilarity(string typeName)
    {
        Type documentType = typeName == nameof(MetadataVectorDocument)
            ? typeof(MetadataVectorDocument)
            : typeof(ContentVectorDocument);
        PropertyInfo vectorProperty = documentType.GetProperty(nameof(MetadataVectorDocument.Vector))!;
        VectorStoreVectorAttribute attribute =
            vectorProperty.GetCustomAttribute<VectorStoreVectorAttribute>()!;

        Assert.Equal(SemanticConstants.EmbeddingDimensions, attribute.Dimensions);
        Assert.Equal(384, attribute.Dimensions);
        Assert.Equal(
            Microsoft.Extensions.VectorData.DistanceFunction.CosineSimilarity,
            attribute.DistanceFunction);
    }

    [Fact]
    public async Task ReplaceAsyncPopulatesBothMetadataAndContentCollectionsIndependently()
    {
        InMemorySemanticVectorIndex index = new();
        Guid metadataOnlyId = Guid.NewGuid();
        Guid contentOwnerId = Guid.NewGuid();
        MetadataRecord metadataOnly = TestData.Movie(id: metadataOnlyId, title: "Metadata Only");
        MetadataRecord contentOwner = TestData.Movie(id: contentOwnerId, title: "Content Owner");
        ContentChunk chunk = TestData.Chunk(contentOwnerId, text: "chunk body");
        await index.ReplaceAsync(BuildSnapshot(
            [(metadataOnly, BasisVector(0)), (contentOwner, BasisVector(2))],
            [(chunk, BasisVector(1))]));

        IVectorSearchReadSession session = index.OpenReadSession();
        IReadOnlyList<MetadataMatch> metadataMatches =
            await session.SearchMetadataAsync(
                BasisVector(0),
                count: 5,
                new HashSet<Guid> { metadataOnlyId, contentOwnerId },
                CancellationToken.None);
        IReadOnlyList<ContentMatch> contentMatches =
            await session.SearchContentAsync(
                BasisVector(1),
                count: 5,
                new HashSet<Guid> { metadataOnlyId, contentOwnerId },
                CancellationToken.None);

        Assert.Equal(metadataOnlyId, metadataMatches[0].Record.Id);
        Assert.Equal(chunk.Id, Assert.Single(contentMatches).Chunk.Id);

        // Each collection only ever surfaces its own document type; a
        // metadata-only item is invisible to a content search and there is
        // no content in the metadata collection to leak back.
        IReadOnlyList<ContentMatch> contentSearchWithMetadataVector =
            await session.SearchContentAsync(
                BasisVector(0),
                5,
                new HashSet<Guid> { metadataOnlyId, contentOwnerId },
                CancellationToken.None);
        Assert.All(
            contentSearchWithMetadataVector,
            match => Assert.Equal(chunk.Id, match.Chunk.Id));
        IReadOnlyList<MetadataMatch> metadataSearchWithContentVector =
            await session.SearchMetadataAsync(
                BasisVector(1),
                5,
                new HashSet<Guid> { metadataOnlyId, contentOwnerId },
                CancellationToken.None);
        Assert.Equal(
            new HashSet<Guid> { metadataOnlyId, contentOwnerId },
            metadataSearchWithContentVector.Select(static match => match.Record.Id).ToHashSet());
    }

    [Fact]
    public async Task SearchMetadataAsyncReturnsClosestMatchesInDescendingRankOrder()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord itemA = TestData.Movie(title: "A");
        MetadataRecord itemB = TestData.Movie(title: "B");
        MetadataRecord itemC = TestData.Movie(title: "C");
        await index.ReplaceAsync(BuildSnapshot(
            [(itemA, BasisVector(0)), (itemB, BasisVector(1)), (itemC, BasisVector(2))],
            []));

        // Query mostly aligned with axis 0 and slightly with axis 1: A should
        // rank strictly ahead of B.
        float[] query = WeightedVector((0, 0.9F), (1, 0.1F));
        IReadOnlyList<MetadataMatch> matches =
            await index.OpenReadSession().SearchMetadataAsync(
                query,
                count: 2,
                new HashSet<Guid> { itemA.Id, itemB.Id, itemC.Id },
                CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Equal(itemA.Id, matches[0].Record.Id);
        Assert.Equal(itemB.Id, matches[1].Record.Id);
        Assert.Equal(0, matches[0].Rank);
        Assert.Equal(1, matches[1].Rank);
        Assert.True(matches[0].Similarity > matches[1].Similarity);
    }

    [Fact]
    public async Task SearchContentAsyncReturnsClosestMatchesInDescendingRankOrder()
    {
        InMemorySemanticVectorIndex index = new();
        Guid ownerId = Guid.NewGuid();
        MetadataRecord owner = TestData.Movie(id: ownerId, title: "Owner");
        ContentChunk chunkA = TestData.Chunk(ownerId, ordinal: 0, text: "chunk a");
        ContentChunk chunkB = TestData.Chunk(ownerId, ordinal: 1, text: "chunk b");
        await index.ReplaceAsync(BuildSnapshot(
            [(owner, BasisVector(2))],
            [(chunkA, BasisVector(0)), (chunkB, BasisVector(1))]));

        float[] query = WeightedVector((0, 0.9F), (1, 0.1F));
        IReadOnlyList<ContentMatch> matches =
            await index.OpenReadSession().SearchContentAsync(
                query,
                count: 2,
                new HashSet<Guid> { ownerId },
                CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Equal(chunkA.Id, matches[0].Chunk.Id);
        Assert.Equal(chunkB.Id, matches[1].Chunk.Id);
    }

    [Fact]
    public async Task GetMetadataAsyncReturnsPersistedRecordOrNullWhenMissing()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord item = TestData.Movie(title: "Found");
        await index.ReplaceAsync(BuildSnapshot([(item, BasisVector(0))], []));

        IVectorSearchReadSession session = index.OpenReadSession();
        MetadataRecord? found = await session.GetMetadataAsync(item.Id, CancellationToken.None);
        MetadataRecord? missing = await session.GetMetadataAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(item.Title, found!.Title);
        Assert.Null(missing);
    }

    [Fact]
    public async Task GetMetadataAsyncResolvesRecordEvenWhenNotAmongTopSearchResults()
    {
        // Content-only owner resolution (as used by SemanticDiscoveryService)
        // goes through this direct id lookup, not through vector search
        // ranking, so it must succeed regardless of how many other items
        // would otherwise crowd it out of a small top-N search.
        InMemorySemanticVectorIndex index = new();
        MetadataRecord[] crowd = Enumerable.Range(0, 5)
            .Select(i => TestData.Movie(title: $"Crowd {i}"))
            .ToArray();
        MetadataRecord target = TestData.Movie(title: "Target");
        (MetadataRecord Record, float[] Vector)[] entries = crowd
            .Select((record, i) => (record, BasisVector(i)))
            .Append((target, BasisVector(10)))
            .ToArray();
        await index.ReplaceAsync(BuildSnapshot(entries, []));

        MetadataRecord? resolved = await index.OpenReadSession()
            .GetMetadataAsync(target.Id, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("Target", resolved!.Title);
    }

    [Fact]
    public async Task GetMetadataVectorAsyncReturnsPersistedVectorOrNullWhenMissing()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord item = TestData.Movie(title: "Found");
        float[] vector = BasisVector(3);
        await index.ReplaceAsync(BuildSnapshot([(item, vector)], []));

        ReadOnlyMemory<float>? found =
            await index.OpenReadSession().GetMetadataVectorAsync(item.Id, CancellationToken.None);
        ReadOnlyMemory<float>? missing =
            await index.OpenReadSession().GetMetadataVectorAsync(
                Guid.NewGuid(),
                CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(vector, found!.Value.ToArray());
        Assert.Null(missing);
    }

    [Fact]
    public async Task ReplaceAsyncReplacesPriorStateEntirely()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord original = TestData.Movie(title: "Original");
        MetadataRecord replacement = TestData.Movie(title: "Replacement");
        await index.ReplaceAsync(BuildSnapshot([(original, BasisVector(0))], []));
        await index.ReplaceAsync(BuildSnapshot([(replacement, BasisVector(0))], []));

        IVectorSearchReadSession session = index.OpenReadSession();
        Assert.Null(await session.GetMetadataAsync(original.Id, CancellationToken.None));
        MetadataRecord? found = await session.GetMetadataAsync(replacement.Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("Replacement", found!.Title);
    }

    [Fact]
    public async Task ReplaceAsyncDefensivelyCopiesSourceRecordsAndVectors()
    {
        InMemorySemanticVectorIndex index = new();
        string[] genres = ["original"];
        float[] vector = BasisVector(0);
        MetadataRecord item = TestData.Movie(title: "Immutable", genres: genres);
        await index.ReplaceAsync(BuildSnapshot([(item, vector)], []));

        genres[0] = "mutated";
        vector[0] = 0F;
        vector[1] = 1F;

        IVectorSearchReadSession session = index.OpenReadSession();
        MetadataRecord stored =
            Assert.IsType<MetadataRecord>(
                await session.GetMetadataAsync(item.Id, CancellationToken.None));
        ReadOnlyMemory<float> storedVector =
            Assert.IsType<ReadOnlyMemory<float>>(
                await session.GetMetadataVectorAsync(item.Id, CancellationToken.None));
        Assert.Equal(["original"], stored.Genres);
        Assert.Equal(1F, storedVector.Span[0]);
        Assert.Equal(0F, storedVector.Span[1]);
    }

    [Fact]
    public async Task ReadSessionReturnsDefensiveRecordsAndVectors()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord item = TestData.Movie(title: "Immutable", genres: ["original"]);
        await index.ReplaceAsync(BuildSnapshot([(item, BasisVector(0))], []));
        IVectorSearchReadSession session = index.OpenReadSession();

        MetadataRecord first =
            Assert.IsType<MetadataRecord>(
                await session.GetMetadataAsync(item.Id, CancellationToken.None));
        first.Genres[0] = "mutated";
        ReadOnlyMemory<float> firstVector =
            Assert.IsType<ReadOnlyMemory<float>>(
                await session.GetMetadataVectorAsync(item.Id, CancellationToken.None));
        Assert.True(MemoryMarshal.TryGetArray(firstVector, out ArraySegment<float> segment));
        segment.Array![segment.Offset] = 0F;

        MetadataRecord second =
            Assert.IsType<MetadataRecord>(
                await session.GetMetadataAsync(item.Id, CancellationToken.None));
        ReadOnlyMemory<float> secondVector =
            Assert.IsType<ReadOnlyMemory<float>>(
                await session.GetMetadataVectorAsync(item.Id, CancellationToken.None));
        Assert.Equal(["original"], second.Genres);
        Assert.Equal(1F, secondVector.Span[0]);

        MetadataMatch firstMatch = Assert.Single(await session.SearchMetadataAsync(
            BasisVector(0),
            1,
            new HashSet<Guid> { item.Id },
            CancellationToken.None));
        firstMatch.Record.Genres[0] = "search mutation";
        MetadataMatch secondMatch = Assert.Single(await session.SearchMetadataAsync(
            BasisVector(0),
            1,
            new HashSet<Guid> { item.Id },
            CancellationToken.None));
        Assert.Equal(["original"], secondMatch.Record.Genres);
    }

    [Fact]
    public async Task ReadSessionRemainsOnOneGenerationAcrossPublication()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord itemA = TestData.Movie(title: "Generation A");
        ContentChunk chunkA = TestData.Chunk(itemA.Id, text: "content a");
        await index.ReplaceAsync(BuildSnapshot(
            [(itemA, BasisVector(0))],
            [(chunkA, BasisVector(0))]));
        IVectorSearchReadSession generationA = index.OpenReadSession();

        MetadataRecord itemB = TestData.Movie(title: "Generation B");
        ContentChunk chunkB = TestData.Chunk(itemB.Id, text: "content b");
        await index.ReplaceAsync(BuildSnapshot(
            [(itemB, BasisVector(1))],
            [(chunkB, BasisVector(1))]));
        IVectorSearchReadSession generationB = index.OpenReadSession();

        Assert.Equal(
            [itemA.Id],
            (await generationA.GetMetadataRecordsAsync(CancellationToken.None))
                .Select(static record => record.Id));
        Assert.Equal(
            itemA.Id,
            Assert.Single(await generationA.SearchMetadataAsync(
                BasisVector(0),
                1,
                new HashSet<Guid> { itemA.Id, itemB.Id },
                CancellationToken.None)).Record.Id);
        Assert.Equal(
            chunkA.Id,
            Assert.Single(await generationA.SearchContentAsync(
                BasisVector(0),
                1,
                new HashSet<Guid> { itemA.Id, itemB.Id },
                CancellationToken.None)).Chunk.Id);
        Assert.NotNull(await generationA.GetMetadataAsync(
            itemA.Id,
            CancellationToken.None));
        Assert.NotNull(await generationA.GetMetadataVectorAsync(
            itemA.Id,
            CancellationToken.None));

        Assert.Equal(
            [itemB.Id],
            (await generationB.GetMetadataRecordsAsync(CancellationToken.None))
                .Select(static record => record.Id));
        Assert.Null(await generationB.GetMetadataAsync(
            itemA.Id,
            CancellationToken.None));
        Assert.Equal(
            itemB.Id,
            Assert.Single(await generationB.SearchMetadataAsync(
                BasisVector(1),
                1,
                new HashSet<Guid> { itemA.Id, itemB.Id },
                CancellationToken.None)).Record.Id);
        Assert.Equal(
            chunkB.Id,
            Assert.Single(await generationB.SearchContentAsync(
                BasisVector(1),
                1,
                new HashSet<Guid> { itemA.Id, itemB.Id },
                CancellationToken.None)).Chunk.Id);
    }

    [Fact]
    public async Task SearchFiltersAllowedOwnersBeforeTopK()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord allowed = TestData.Movie(title: "Allowed");
        ContentChunk allowedChunk = TestData.Chunk(allowed.Id, text: "allowed content");
        MetadataRecord[] crowd = Enumerable.Range(0, 40)
            .Select(index => TestData.Movie(title: $"Denied {index}"))
            .ToArray();
        float[] allowedVector = WeightedVector((0, 0.8F), (1, 0.2F));
        await index.ReplaceAsync(BuildSnapshot(
            crowd.Select(record => (record, BasisVector(0)))
                .Append((allowed, allowedVector))
                .ToArray(),
            [(allowedChunk, allowedVector)]));
        IVectorSearchReadSession session = index.OpenReadSession();
        HashSet<Guid> allowedIds = [allowed.Id];

        Assert.Equal(
            allowed.Id,
            Assert.Single(await session.SearchMetadataAsync(
                BasisVector(0),
                1,
                allowedIds,
                CancellationToken.None)).Record.Id);
        Assert.Equal(
            allowedChunk.Id,
            Assert.Single(await session.SearchContentAsync(
                BasisVector(0),
                1,
                allowedIds,
                CancellationToken.None)).Chunk.Id);
    }

    [Fact]
    public async Task PreparedStateCanOnlyBePublishedOnceUnderConcurrency()
    {
        InMemorySemanticVectorIndex index = new();
        MetadataRecord item = TestData.Movie();
        InMemorySemanticVectorIndex.PreparedState prepared =
            await index.PrepareAsync(BuildSnapshot([(item, BasisVector(0))], []));
        Exception?[] outcomes = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                try
                {
                    index.Publish(prepared);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            })));

        Assert.Single(outcomes, static outcome => outcome is null);
        Assert.Single(outcomes, static outcome => outcome is InvalidOperationException);
    }

    [Fact]
    public async Task SearchMetadataAsyncRejectsVectorWithWrongDimensions()
    {
        InMemorySemanticVectorIndex index = new();
        await index.ReplaceAsync(BuildSnapshot([], []));

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => index.OpenReadSession()
                .SearchMetadataAsync(
                    new float[16],
                    5,
                    new HashSet<Guid>(),
                    CancellationToken.None).AsTask());

        Assert.Contains("384", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchContentAsyncRejectsVectorWithWrongDimensions()
    {
        InMemorySemanticVectorIndex index = new();
        await index.ReplaceAsync(BuildSnapshot([], []));

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => index.OpenReadSession()
                .SearchContentAsync(
                    new float[16],
                    5,
                    new HashSet<Guid>(),
                    CancellationToken.None).AsTask());

        Assert.Contains("384", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task SearchRejectsNonFiniteQueryValues(float invalid)
    {
        float[] vector = BasisVector(0);
        vector[1] = invalid;
        IVectorSearchReadSession session =
            new InMemorySemanticVectorIndex().OpenReadSession();

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.SearchMetadataAsync(
                vector,
                1,
                new HashSet<Guid>(),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SearchRejectsZeroVector()
    {
        IVectorSearchReadSession session =
            new InMemorySemanticVectorIndex().OpenReadSession();

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.SearchMetadataAsync(
                new float[SemanticConstants.EmbeddingDimensions],
                1,
                new HashSet<Guid>(),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SearchRejectsNonPositiveCount()
    {
        IVectorSearchReadSession session =
            new InMemorySemanticVectorIndex().OpenReadSession();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.SearchMetadataAsync(
                BasisVector(0),
                0,
                new HashSet<Guid>(),
                CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(1e300)]
    [InlineData(-1e300)]
    [InlineData(1.1)]
    public void ConvertScoreRejectsValuesThatOverflowOrExceedCosineRange(
        double score)
    {
        Assert.Throws<InvalidOperationException>(
                () => InMemorySemanticVectorIndex.ConvertScore(score));
    }

    private static float[] BasisVector(int index)
    {
        float[] vector = new float[SemanticConstants.EmbeddingDimensions];
        vector[index] = 1F;
        return vector;
    }

    private static float[] WeightedVector(params (int Index, float Weight)[] components)
    {
        float[] vector = new float[SemanticConstants.EmbeddingDimensions];
        foreach ((int index, float weight) in components)
        {
            vector[index] = weight;
        }

        VectorMath.Normalize(vector);
        return vector;
    }

    private static IndexSnapshot BuildSnapshot(
        IReadOnlyList<(MetadataRecord Record, float[] Vector)> metadata,
        IReadOnlyList<(ContentChunk Chunk, float[] Vector)> content) =>
        TestData.Seal(new IndexSnapshot
        {
            Identity = TestData.Identity(),
            CreatedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            Metadata = metadata
                .Select(entry => new PersistedMetadataVector
                {
                    Record = entry.Record,
                    ProjectionHash = ContentHash.Compute(entry.Record.Title),
                    Vector = entry.Vector,
                })
                .ToArray(),
            Content = content
                .Select(entry => new PersistedContentVector
                {
                    Chunk = entry.Chunk,
                    Vector = entry.Vector,
                })
                .ToArray(),
        });
}
