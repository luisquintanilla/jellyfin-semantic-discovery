using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Jellyfin.SemanticDiscovery.Core.VectorData;

namespace Jellyfin.SemanticDiscovery.Core.Tests.VectorData;

/// <summary>
/// Additional two-phase prepare/publish guard-rail coverage for
/// <see cref="InMemorySemanticVectorIndex"/>, kept separate from
/// <see cref="InMemorySemanticVectorIndexTests"/> so both can evolve
/// independently: a prepared state can only ever be published by the index
/// that created it, and cancellation observed while preparing never mutates
/// or publishes anything.
/// </summary>
public sealed class InMemorySemanticVectorIndexPublishGuardTests
{
    [Fact]
    public async Task PublishRejectsAPreparedStateFromAnotherIndexAndLeavesBothIndexesUntouched()
    {
        InMemorySemanticVectorIndex originatingIndex = new();
        InMemorySemanticVectorIndex otherIndex = new();
        MetadataRecord originalOnOther = TestData.Movie(title: "Other's Original");
        await otherIndex.ReplaceAsync(
            BuildSnapshot([(originalOnOther, BasisVector(0))], []));

        MetadataRecord item = TestData.Movie(title: "Prepared On Originating");
        InMemorySemanticVectorIndex.PreparedState prepared =
            await originatingIndex.PrepareAsync(BuildSnapshot([(item, BasisVector(1))], []));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => otherIndex.Publish(prepared));
        Assert.Contains("originating index", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Neither index observed any change from the rejected publish.
        Assert.Null(await otherIndex.OpenReadSession()
            .GetMetadataAsync(item.Id, CancellationToken.None));
        Assert.NotNull(await otherIndex.OpenReadSession()
            .GetMetadataAsync(originalOnOther.Id, CancellationToken.None));
        Assert.Null(await originatingIndex.OpenReadSession()
            .GetMetadataAsync(item.Id, CancellationToken.None));

        // The prepared state can still be published on its own originating
        // index afterwards.
        originatingIndex.Publish(prepared);
        Assert.NotNull(await originatingIndex.OpenReadSession()
            .GetMetadataAsync(item.Id, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareAsyncWithAnAlreadyCancelledTokenNeverPublishesEvenIfItCompletes()
    {
        // CommunityToolkit's in-memory collection does not observe
        // cancellation for a small, effectively-synchronous payload, so
        // PrepareAsync itself may still complete even with an
        // already-cancelled token. The real safety net against
        // cancellation "before/during preparation" is that
        // SemanticIndexBuilder never calls Publish on a PreparedState it
        // did not reach via its own explicit pre-PrepareAsync
        // cancellation checkpoint (see SemanticIndexBuilderTests). This
        // test pins the narrower, always-true guarantee at this layer:
        // merely preparing state — regardless of whether that call
        // observed cancellation — can never itself mutate the live index;
        // only an explicit Publish call can.
        InMemorySemanticVectorIndex index = new();
        MetadataRecord baseline = TestData.Movie(title: "Baseline");
        await index.ReplaceAsync(BuildSnapshot([(baseline, BasisVector(0))], []));

        MetadataRecord replacement = TestData.Movie(title: "Should Never Publish");
        using CancellationTokenSource alreadyCancelled = new();
        await alreadyCancelled.CancelAsync();

        try
        {
            await index.PrepareAsync(
                BuildSnapshot([(replacement, BasisVector(1))], []),
                alreadyCancelled.Token);
        }
        catch (OperationCanceledException)
        {
            // Also acceptable: preparation itself observed the cancellation.
        }

        IVectorSearchReadSession session = index.OpenReadSession();
        Assert.NotNull(await session.GetMetadataAsync(baseline.Id, CancellationToken.None));
        Assert.Null(await session.GetMetadataAsync(replacement.Id, CancellationToken.None));
        Assert.Equal(
            [baseline.Id],
            (await session.GetMetadataRecordsAsync(CancellationToken.None))
                .Select(static record => record.Id));
    }

    private static float[] BasisVector(int index)
    {
        float[] vector = new float[SemanticConstants.EmbeddingDimensions];
        vector[index] = 1F;
        return vector;
    }

    private static IndexSnapshot BuildSnapshot(
        IReadOnlyList<(MetadataRecord Record, float[] Vector)> metadata,
        IReadOnlyList<(Domain.ContentChunk Chunk, float[] Vector)> content) =>
        TestData.Seal(new IndexSnapshot
        {
            Identity = TestData.Identity(),
            CreatedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            Metadata = metadata
                .Select(entry => new PersistedMetadataVector
                {
                    Record = entry.Record,
                    ProjectionHash = Identity.ContentHash.Compute($"hash-{entry.Record.Id}"),
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
