using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Jellyfin.SemanticDiscovery.Core.VectorData;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Indexing;

/// <summary>
/// A scripted <see cref="IIndexSnapshotStore"/> that delegates to a real
/// inner store (so disk state stays observable/verifiable through a plain
/// <see cref="AtomicSnapshotStore"/> at the same path) while recording every
/// call and optionally invoking a hook immediately after a durable
/// <see cref="SaveAsync"/> completes.
/// </summary>
public sealed class RecordingSnapshotStore(IIndexSnapshotStore inner) : IIndexSnapshotStore
{
    public List<IndexSnapshot> SaveCalls { get; } = [];

    public int LoadCallCount { get; private set; }

    public Action? OnAfterDurableSave { get; set; }

    public async ValueTask<IndexSnapshot?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        LoadCallCount++;
        return await inner.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveAsync(
        IndexSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await inner.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        SaveCalls.Add(snapshot);
        OnAfterDurableSave?.Invoke();
    }
}

/// <summary>
/// A minimal embedding generator (reusing <see cref="RecordingEmbeddingGenerator"/>'s
/// deterministic vector derivation) that invokes a callback immediately
/// after each batch completes — used to trigger cancellation precisely at
/// the boundary between "all embedding work is done" and
/// "<c>PrepareAsync</c>/<c>SaveAsync</c> begin".
/// </summary>
public sealed class CallbackEmbeddingGenerator(Action? afterEachCall = null) :
    IEmbeddingGenerator<string, Embedding<float>>
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string[] inputs = values.ToArray();
        Calls.Add(inputs);
        GeneratedEmbeddings<Embedding<float>> generated = new(inputs.Length);
        foreach (string input in inputs)
        {
            generated.Add(new Embedding<float>(RecordingEmbeddingGenerator.CreateVector(input))
            {
                ModelId = "callback-fake",
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }

        afterEachCall?.Invoke();
        return Task.FromResult(generated);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Regression coverage for the two-phase prepare/save/publish contract
/// introduced by <see cref="InMemorySemanticVectorIndex.PrepareAsync"/> +
/// <c>Publish</c>: an invalid rebuild, or one cancelled before reaching the
/// prepare/save boundary, must leave both the on-disk snapshot and the live
/// vector index exactly as they were; and once <c>SaveAsync</c> has
/// durably committed, publication of the prepared state to the live index
/// is no longer cancellable.
/// </summary>
public sealed class SemanticIndexBuilderTwoPhasePublishTests
{
    [Fact]
    public async Task RebuildAsyncInvalidReplacementNeverChangesLiveIndexOrDisk()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid itemId = Guid.NewGuid();
        MetadataRecord baseline = TestData.Movie(id: itemId, title: "Baseline");
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = baseline },
        ];
        RecordingSnapshotStore snapshotStore = new(harness.CreateSnapshotStore());
        SemanticIndexBuilder builder = harness.CreateBuilder(
            TestData.Identity(),
            snapshotStore: snapshotStore);
        await builder.RebuildAsync();
        int saveCallsAfterBaseline = snapshotStore.SaveCalls.Count;

        // The validator rejects an empty (rather than absent) LibraryId —
        // this makes PrepareAsync throw before SaveAsync is ever reached.
        harness.Source.Items =
        [
            new IndexSourceItem
            {
                Metadata = TestData.Movie(id: itemId, title: "Baseline") with
                {
                    LibraryId = Guid.Empty,
                },
            },
        ];

        await Assert.ThrowsAsync<InvalidDataException>(() => builder.RebuildAsync().AsTask());

        Assert.Equal(saveCallsAfterBaseline, snapshotStore.SaveCalls.Count);
        IndexSnapshot? onDisk = await harness.CreateSnapshotStore().LoadAsync();
        Assert.NotNull(onDisk);
        MetadataRecord onDiskRecord = Assert.Single(onDisk!.Metadata).Record;
        Assert.Equal(baseline.LibraryId, onDiskRecord.LibraryId);
        Assert.Equal(baseline.ParentId, onDiskRecord.ParentId);
        Assert.Equal("Baseline", onDiskRecord.Title);

        IReadOnlyList<MetadataRecord> live = await harness.VectorIndex
            .OpenReadSession()
            .GetMetadataRecordsAsync(CancellationToken.None);
        MetadataRecord liveRecord = Assert.Single(live);
        Assert.Equal(baseline.LibraryId, liveRecord.LibraryId);
        Assert.Equal(baseline.ParentId, liveRecord.ParentId);
        Assert.Equal("Baseline", liveRecord.Title);
    }

    [Fact]
    public async Task RebuildAsyncCancelledRightAtThePrepareBoundaryPreservesDiskAndLiveState()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid itemId = Guid.NewGuid();
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = TestData.Movie(id: itemId, title: "Baseline") },
        ];
        RecordingSnapshotStore baselineStore = new(harness.CreateSnapshotStore());
        SemanticIndexBuilder baselineBuilder = harness.CreateBuilder(
            TestData.Identity(),
            snapshotStore: baselineStore);
        await baselineBuilder.RebuildAsync();

        using CancellationTokenSource cancellation = new();
        RecordingSnapshotStore cancelledStore = new(harness.CreateSnapshotStore());
        CallbackEmbeddingGenerator cancellingGenerator = new(
            afterEachCall: () => cancellation.Cancel());
        harness.Source.Items =
        [
            new IndexSourceItem
            {
                Metadata = TestData.Movie(id: itemId, title: "Baseline") with
                {
                    Overview = "Changed so metadata actually needs re-embedding.",
                },
            },
        ];
        SemanticIndexBuilder cancellingBuilder = new(
            cancellingGenerator,
            harness.TokenCounter,
            harness.Source,
            [harness.SubtitleReader, harness.BookReader],
            cancelledStore,
            harness.VectorIndex,
            TestData.Identity());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cancellingBuilder.RebuildAsync(null, cancellation.Token).AsTask());

        // Embedding actually ran (proving cancellation was observed at the
        // prepare/save boundary, not earlier), but neither SaveAsync nor a
        // live-index publish ever happened.
        Assert.NotEmpty(cancellingGenerator.Calls);
        Assert.Empty(cancelledStore.SaveCalls);

        IndexSnapshot? onDisk = await harness.CreateSnapshotStore().LoadAsync();
        Assert.NotNull(onDisk);
        Assert.Null(Assert.Single(onDisk!.Metadata).Record.Overview);

        IReadOnlyList<MetadataRecord> live = await harness.VectorIndex
            .OpenReadSession()
            .GetMetadataRecordsAsync(CancellationToken.None);
        Assert.Null(Assert.Single(live).Overview);
    }

    [Fact]
    public async Task RebuildAsyncPublishesSynchronouslyEvenWhenTheStoreCancelsTheCallerTokenAfterDurableSave()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid itemId = Guid.NewGuid();
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = TestData.Movie(id: itemId, title: "Updated Title") },
        ];
        using CancellationTokenSource cancellation = new();
        RecordingSnapshotStore snapshotStore = new(harness.CreateSnapshotStore())
        {
            OnAfterDurableSave = () => cancellation.Cancel(),
        };
        SemanticIndexBuilder builder = harness.CreateBuilder(
            TestData.Identity(),
            snapshotStore: snapshotStore);

        // The token is cancelled by the store itself, from inside SaveAsync,
        // immediately after the durable write completes — after the point
        // of no return. RebuildAsync must still finish successfully and
        // publish to the live index rather than surfacing a spurious
        // OperationCanceledException for a token that raced the commit.
        IndexBuildResult result = await builder.RebuildAsync(null, cancellation.Token);

        Assert.Equal(1, result.MetadataCount);
        Assert.Single(snapshotStore.SaveCalls);
        IReadOnlyList<MetadataRecord> live = await harness.VectorIndex
            .OpenReadSession()
            .GetMetadataRecordsAsync(CancellationToken.None);
        Assert.Equal("Updated Title", Assert.Single(live).Title);
    }
}
