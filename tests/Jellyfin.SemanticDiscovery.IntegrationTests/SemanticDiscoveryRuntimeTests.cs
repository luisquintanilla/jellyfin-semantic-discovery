using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Plugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.SemanticDiscovery.IntegrationTests;

public sealed class SemanticDiscoveryRuntimeTests
{
    [Fact]
    public async Task RebuildCancellationCannotOverwriteQueuedRebuildStatus()
    {
        TaskCompletionSource firstEntered = NewSignal();
        TaskCompletionSource secondEntered = NewSignal();
        TaskCompletionSource releaseSecond = NewSignal();
        ScriptedRuntimeState state = new()
        {
            Rebuild = async (call, cancellationToken) =>
            {
                if (call == 1)
                {
                    firstEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                secondEntered.SetResult();
                await releaseSecond.Task.WaitAsync(cancellationToken);
                return Success();
            },
        };
        using SemanticDiscoveryRuntime runtime = CreateRuntime(state);
        using CancellationTokenSource cancelFirst = new();

        Task first = runtime.RebuildAsync(null, cancelFirst.Token);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = runtime.RebuildAsync(null, CancellationToken.None);
        await cancelFirst.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Building index", runtime.Status);
        releaseSecond.SetResult();
        await second;
        Assert.StartsWith("Ready:", runtime.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationWhileWaitingForRebuildGateDoesNotRewriteActiveStatus()
    {
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        ScriptedRuntimeState state = new()
        {
            Rebuild = async (_, cancellationToken) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Success();
            },
        };
        using SemanticDiscoveryRuntime runtime = CreateRuntime(state);
        Task active = runtime.RebuildAsync(null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using CancellationTokenSource waitingCancellation = new();
        Task waiting = runtime.RebuildAsync(null, waitingCancellation.Token);

        await waitingCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        Assert.Equal("Building index", runtime.Status);
        release.SetResult();
        await active;
    }

    [Fact]
    public async Task RebuildSetsBuildingOnlyAfterLazyInitializationCompletes()
    {
        TaskCompletionSource releaseFactory = NewSignal();
        TaskCompletionSource rebuildEntered = NewSignal();
        TaskCompletionSource releaseRebuild = NewSignal();
        ScriptedRuntimeState state = new()
        {
            Rebuild = async (_, cancellationToken) =>
            {
                rebuildEntered.SetResult();
                await releaseRebuild.Task.WaitAsync(cancellationToken);
                return Success();
            },
        };
        using SemanticDiscoveryRuntime runtime = new(
            async cancellationToken =>
            {
                await releaseFactory.Task.WaitAsync(cancellationToken);
                return state;
            },
            NullLogger<SemanticDiscoveryRuntime>.Instance,
            TimeSpan.FromSeconds(2));

        Task rebuild = runtime.RebuildAsync(null, CancellationToken.None);
        Assert.Equal("Not indexed", runtime.Status);
        releaseFactory.SetResult();
        await rebuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Building index", runtime.Status);
        releaseRebuild.SetResult();
        await rebuild;
    }

    [Fact]
    public async Task CancelledRebuildReportsSnapshotLoadedDuringInitializationAsActive()
    {
        TaskCompletionSource entered = NewSignal();
        ScriptedRuntimeState state = new()
        {
            HasIndex = true,
            Rebuild = async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Success();
            },
        };
        using SemanticDiscoveryRuntime runtime = CreateRuntime(state);
        using CancellationTokenSource cancellation = new();

        Task rebuild = runtime.RebuildAsync(null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rebuild);

        Assert.Equal(
            "Rebuild cancelled; previous index remains active",
            runtime.Status);
    }

    [Fact]
    public async Task DisposeTimeoutLeavesStateIntactAndLaterRetryDrainsAndDisposes()
    {
        TaskCompletionSource searchEntered = NewSignal();
        TaskCompletionSource releaseSearch = NewSignal();
        ScriptedRuntimeState state = new()
        {
            HasIndex = true,
            Search = async cancellationToken =>
            {
                searchEntered.SetResult();
                await releaseSearch.Task.WaitAsync(cancellationToken);
                return [];
            },
        };
        SemanticDiscoveryRuntime runtime = CreateRuntime(
            state,
            TimeSpan.FromMilliseconds(100));
        Task search = runtime.SearchAsync(
            "query",
            new SearchScope(),
            1,
            CancellationToken.None).AsTask();
        await searchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(
            () => Task.Run(runtime.Dispose));
        Assert.Equal(0, state.DisposeCount);

        releaseSearch.SetResult();
        await search;
        runtime.Dispose();
        runtime.Dispose();
        Assert.Equal(1, state.DisposeCount);
    }

    [Fact]
    public async Task FactoryFailureDoesNotLeaveAnAcceptedOperationThatBlocksDisposal()
    {
        IOException expected = new("construction failed");
        SemanticDiscoveryRuntime runtime = new(
            _ => ValueTask.FromException<ISemanticDiscoveryRuntimeState>(expected),
            NullLogger<SemanticDiscoveryRuntime>.Instance,
            TimeSpan.FromSeconds(1));

        IOException actual = await Assert.ThrowsAsync<IOException>(
            () => runtime.SearchAsync(
                "query",
                new SearchScope(),
                1,
                CancellationToken.None).AsTask());
        runtime.Dispose();

        Assert.Same(expected, actual);
        Assert.Equal(
            "Semantic runtime initialization failed",
            runtime.Status);
    }

    [Fact]
    public async Task FactoryFailureSetsInitializationStatusForSimilarItems()
    {
        using SemanticDiscoveryRuntime runtime = CreateFailingRuntime();

        await Assert.ThrowsAsync<IOException>(
            () => runtime.GetSimilarAsync(
                Guid.NewGuid(),
                new SearchScope(),
                new HashSet<Guid>(),
                1,
                CancellationToken.None).AsTask());

        Assert.Equal(
            "Semantic runtime initialization failed",
            runtime.Status);
    }

    [Fact]
    public async Task FactoryFailureSetsInitializationStatusForRebuildWithoutStaleBuilding()
    {
        using SemanticDiscoveryRuntime runtime = CreateFailingRuntime();

        await Assert.ThrowsAsync<IOException>(
            () => runtime.RebuildAsync(
                null,
                CancellationToken.None));

        Assert.Equal(
            "Semantic runtime initialization failed",
            runtime.Status);
    }

    [Fact]
    public async Task DisposeWaitsForABlockedFactoryInitializationBeforeDisposingTheCreatedState()
    {
        TaskCompletionSource factoryEntered = NewSignal();
        TaskCompletionSource releaseFactory = NewSignal();
        ScriptedRuntimeState state = new();
        SemanticDiscoveryRuntime runtime = new(
            async cancellationToken =>
            {
                factoryEntered.SetResult();
                await releaseFactory.Task.WaitAsync(cancellationToken);
                return state;
            },
            NullLogger<SemanticDiscoveryRuntime>.Instance,
            TimeSpan.FromSeconds(5));

        // The very first call is still inside the (blocked) state factory —
        // no ISemanticDiscoveryRuntimeState exists yet.
        Task<IReadOnlyList<SemanticSearchResult>> search =
            runtime.SearchAsync("query", new SearchScope(), 1, CancellationToken.None).AsTask();
        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = Task.Run(runtime.Dispose);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, state.DisposeCount);

        releaseFactory.SetResult();
        await search;
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, state.DisposeCount);
    }

    [Fact]
    public async Task DisposeWaitsForABlockedRebuildBeforeDisposingTheState()
    {
        TaskCompletionSource rebuildEntered = NewSignal();
        TaskCompletionSource releaseRebuild = NewSignal();
        ScriptedRuntimeState state = new()
        {
            Rebuild = async (_, cancellationToken) =>
            {
                rebuildEntered.SetResult();
                await releaseRebuild.Task.WaitAsync(cancellationToken);
                return Success();
            },
        };
        SemanticDiscoveryRuntime runtime = CreateRuntime(state, TimeSpan.FromSeconds(5));

        Task rebuild = runtime.RebuildAsync(null, CancellationToken.None);
        await rebuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = Task.Run(runtime.Dispose);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, state.DisposeCount);

        releaseRebuild.SetResult();
        await rebuild;
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, state.DisposeCount);
    }

    [Fact]
    public async Task DisposeWaitsForABlockedSearchWithAGenerousTimeoutThenDisposesExactlyOnce()
    {
        TaskCompletionSource searchEntered = NewSignal();
        TaskCompletionSource releaseSearch = NewSignal();
        ScriptedRuntimeState state = new()
        {
            HasIndex = true,
            Search = async cancellationToken =>
            {
                searchEntered.SetResult();
                await releaseSearch.Task.WaitAsync(cancellationToken);
                return [];
            },
        };
        SemanticDiscoveryRuntime runtime = CreateRuntime(state, TimeSpan.FromSeconds(5));

        Task<IReadOnlyList<SemanticSearchResult>> search =
            runtime.SearchAsync("query", new SearchScope(), 1, CancellationToken.None).AsTask();
        await searchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // With a generous dispose timeout, Dispose must genuinely wait for
        // the blocked search rather than tearing down gates/state under it.
        Task dispose = Task.Run(runtime.Dispose);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, state.DisposeCount);

        releaseSearch.SetResult();
        await search;
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, state.DisposeCount);
    }

    [Fact]
    public async Task OperationsAfterDisposeCompletesThrowObjectDisposedExceptionAndDisposeStaysIdempotent()
    {
        ScriptedRuntimeState state = new() { HasIndex = true };
        SemanticDiscoveryRuntime runtime = CreateRuntime(state);
        await runtime.SearchAsync("warm up", new SearchScope(), 1, CancellationToken.None);

        runtime.Dispose();
        runtime.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => runtime.SearchAsync(
                "query",
                new SearchScope(),
                1,
                CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => runtime.RebuildAsync(null, CancellationToken.None));
        Assert.Equal(1, state.DisposeCount);
    }

    private static SemanticDiscoveryRuntime CreateRuntime(
        ScriptedRuntimeState state,
        TimeSpan? disposeTimeout = null) => new(
            _ => ValueTask.FromResult<ISemanticDiscoveryRuntimeState>(state),
            NullLogger<SemanticDiscoveryRuntime>.Instance,
            disposeTimeout ?? TimeSpan.FromSeconds(2));

    private static SemanticDiscoveryRuntime CreateFailingRuntime() => new(
        _ => ValueTask.FromException<ISemanticDiscoveryRuntimeState>(
            new IOException("SECRET_REMOTE_DOCUMENT_TEXT")),
        NullLogger<SemanticDiscoveryRuntime>.Instance,
        TimeSpan.FromSeconds(2));

    private static IndexBuildResult Success() => new(2, 3, 1, 1, []);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ScriptedRuntimeState : ISemanticDiscoveryRuntimeState
    {
        private int _rebuildCalls;
        private int _disposeCount;

        public bool HasIndex { get; set; }

        public string? InitializationWarning { get; init; }

        public string IndexIdentity => "identity";

        public float RecommendationFloor { get; init; } =
            new SearchScoreCalibration().RecommendationFloor;

        public NetworkBoundaryStatus NetworkBoundary { get; init; } = new(
            ExternalEgressCanaryBlocked: false,
            MarkItDownReachable: true,
            MarkItDownReachabilityProbe: "test",
            Enforcement: "test",
            ResidualLimitation: "test");

        public ClientRequestMemoryObservation? LargestClientRequestMemory =>
            null;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Func<CancellationToken, ValueTask<IReadOnlyList<SemanticSearchResult>>>?
            Search { get; init; }

        public Func<int, CancellationToken, ValueTask<IndexBuildResult>>?
            Rebuild { get; init; }

        public ValueTask<IReadOnlyList<SemanticSearchResult>> SearchAsync(
            string query,
            SearchScope scope,
            int limit,
            CancellationToken cancellationToken) =>
            Search?.Invoke(cancellationToken) ??
            ValueTask.FromResult<IReadOnlyList<SemanticSearchResult>>([]);

        public ValueTask<IReadOnlyList<SimilarItemResult>> GetSimilarAsync(
            Guid itemId,
            SearchScope scope,
            IReadOnlySet<Guid> excludedItemIds,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SimilarItemResult>>([]);

        public ValueTask<IndexBuildResult> RebuildAsync(
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            Rebuild?.Invoke(
                Interlocked.Increment(ref _rebuildCalls),
                cancellationToken) ??
            ValueTask.FromResult(Success());

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
