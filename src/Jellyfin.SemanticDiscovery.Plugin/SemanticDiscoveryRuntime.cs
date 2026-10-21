using System.Diagnostics;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.VectorData;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.SemanticDiscovery.Plugin;

internal interface ISemanticDiscoveryRuntimeState : IDisposable
{
    bool HasIndex { get; set; }

    string? InitializationWarning { get; }

    string IndexIdentity { get; }

    float RecommendationFloor { get; }

    NetworkBoundaryStatus NetworkBoundary { get; }

    ClientRequestMemoryObservation? LargestClientRequestMemory { get; }

    ValueTask<IReadOnlyList<SemanticSearchResult>> SearchAsync(
        string query,
        SearchScope scope,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SimilarItemResult>> GetSimilarAsync(
        Guid itemId,
        SearchScope scope,
        IReadOnlySet<Guid> excludedItemIds,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<IndexBuildResult> RebuildAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public sealed partial class SemanticDiscoveryRuntime : IDisposable
{
    private readonly Func<CancellationToken, ValueTask<ISemanticDiscoveryRuntimeState>>
        _stateFactory;
    private readonly ILogger<SemanticDiscoveryRuntime> _logger;
    private readonly TimeSpan _disposeTimeout;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly Lock _lifetimeLock = new();
    private TaskCompletionSource? _drained;
    private ISemanticDiscoveryRuntimeState? _state;
    private volatile string _status = "Not indexed";
    private int _activeOperations;
    private long _recommendationQueryCount;
    private long _recommendationResultCount;
    private long _queryEmbeddingsTotal;
    private bool _disposing;
    private bool _disposed;

    public SemanticDiscoveryRuntime(
        SemanticDiscoveryPaths paths,
        JellyfinIndexSource source,
        JellyfinMediaAccessPolicy accessPolicy,
        ILogger<SemanticDiscoveryRuntime> logger)
        : this(
            cancellationToken => CreateStateAsync(
                paths ?? throw new ArgumentNullException(nameof(paths)),
                source ?? throw new ArgumentNullException(nameof(source)),
                accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy)),
                cancellationToken),
            logger,
            TimeSpan.FromSeconds(30))
    {
    }

    internal SemanticDiscoveryRuntime(
        Func<CancellationToken, ValueTask<ISemanticDiscoveryRuntimeState>> stateFactory,
        ILogger<SemanticDiscoveryRuntime> logger,
        TimeSpan disposeTimeout)
    {
        _stateFactory =
            stateFactory ?? throw new ArgumentNullException(nameof(stateFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _disposeTimeout = disposeTimeout > TimeSpan.Zero
            ? disposeTimeout
            : throw new ArgumentOutOfRangeException(nameof(disposeTimeout));
        _recommendationQueryCount = Math.Max(
            0,
            Plugin.Instance?.ReadConfiguration(
                static configuration => configuration.RecommendationQueryCount) ?? 0);
        _recommendationResultCount = Math.Max(
            0,
            Plugin.Instance?.ReadConfiguration(
                static configuration => configuration.RecommendationResultCount) ?? 0);
        _queryEmbeddingsTotal = Math.Max(
            0,
            Plugin.Instance?.ReadConfiguration(
                static configuration => configuration.QueryEmbeddingsTotal) ?? 0);
    }

    public string Status => _status;

    public async ValueTask<IReadOnlyList<SemanticSearchResult>> SearchAsync(
        string query,
        SearchScope scope,
        int limit,
        CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            ISemanticDiscoveryRuntimeState state;
            try
            {
                state = await GetStateAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TrySetStatus(
                    "Semantic runtime initialization failed",
                    GetSafeError(exception));
                LogRuntimeFailure(_logger, exception);
                throw;
            }

            if (!state.HasIndex)
            {
                return [];
            }

            try
            {
                IReadOnlyList<SemanticSearchResult> results =
                    await state.SearchAsync(
                    query,
                    scope,
                    limit,
                    cancellationToken).ConfigureAwait(false);
                long queryEmbeddings = Interlocked.Increment(
                    ref _queryEmbeddingsTotal);
                TryPersistQueryEmbeddings(queryEmbeddings);
                return results;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TrySetStatus("Search failed", GetSafeError(exception));
                throw;
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask<IReadOnlyList<SimilarItemResult>> GetSimilarAsync(
        Guid itemId,
        SearchScope scope,
        IReadOnlySet<Guid> excludedItemIds,
        int limit,
        CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            ISemanticDiscoveryRuntimeState state;
            try
            {
                state = await GetStateAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TrySetStatus(
                    "Semantic runtime initialization failed",
                    GetSafeError(exception));
                LogRuntimeFailure(_logger, exception);
                throw;
            }

            if (!state.HasIndex)
            {
                return [];
            }

            try
            {
                IReadOnlyList<SimilarItemResult> results =
                    await state.GetSimilarAsync(
                    itemId,
                    scope,
                    excludedItemIds,
                    limit,
                    cancellationToken).ConfigureAwait(false);
                long queryCount = Interlocked.Increment(
                    ref _recommendationQueryCount);
                long resultCount = Interlocked.Add(
                    ref _recommendationResultCount,
                    results.Count);
                TryPersistRecommendationDiagnostics(
                    queryCount,
                    resultCount,
                    itemId,
                    results,
                    state.RecommendationFloor);

                return results;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TrySetStatus(
                    "Similar-items lookup failed",
                    GetSafeError(exception));
                throw;
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public async Task RebuildAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        EnterOperation();
        bool rebuildGateEntered = false;
        try
        {
            await _rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            rebuildGateEntered = true;
            ISemanticDiscoveryRuntimeState state;
            try
            {
                state = await GetStateAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TrySetStatus(
                    "Semantic runtime initialization failed",
                    GetSafeError(exception));
                LogRuntimeFailure(_logger, exception);
                throw;
            }

            TrySetStatus("Building index");
            IndexBuildResult result;
            try
            {
                result = await state.RebuildAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TrySetStatus(
                    state.HasIndex
                        ? "Rebuild cancelled; previous index remains active"
                        : "Rebuild cancelled; no index is active");
                throw;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TrySetStatus("Index build failed", GetSafeError(exception));
                LogRuntimeFailure(_logger, exception);
                throw;
            }

            state.HasIndex = true;
            string status = result.Errors.Count == 0
                ? $"Ready: {result.MetadataCount} items, {result.ContentChunkCount} content chunks"
                : $"Ready with {result.Errors.Count} content ingestion error(s)";
            SetSuccessfulStatus(
                state,
                result,
                status,
                result.Errors.Count == 0 ? null : result.Errors[0].Message);
            LogRebuildCompleted(
                _logger,
                result.MetadataCount,
                result.ContentChunkCount,
                result.EmbeddedMetadataCount,
                result.EmbeddedContentChunkCount,
                result.Errors.Count);
        }
        finally
        {
            if (rebuildGateEntered)
            {
                _rebuildGate.Release();
            }

            ExitOperation();
        }
    }

    public void Dispose()
    {
        if (!_disposeGate.Wait(_disposeTimeout))
        {
            throw new TimeoutException(
                "Timed out waiting for another caller to dispose Semantic Discovery.");
        }

        try
        {
            Task drain;
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposing = true;
                if (_activeOperations == 0)
                {
                    drain = Task.CompletedTask;
                }
                else
                {
                    _drained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drain = _drained.Task;
                }
            }

            if (!WaitWithoutPropagating(drain, _disposeTimeout))
            {
                throw new TimeoutException(
                    "Timed out draining Semantic Discovery operations; runtime resources were left intact.");
            }

            drain.GetAwaiter().GetResult();
            _state?.Dispose();
            _initializationGate.Dispose();
            _rebuildGate.Dispose();
            lock (_lifetimeLock)
            {
                _disposed = true;
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private async ValueTask<ISemanticDiscoveryRuntimeState> GetStateAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) is { } initialized)
        {
            return initialized;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is not null)
            {
                return _state;
            }

            ISemanticDiscoveryRuntimeState state =
                await _stateFactory(cancellationToken).ConfigureAwait(false);
            bool installed = false;
            try
            {
                TrySetStatus(
                    state.HasIndex ? "Ready from snapshot" : "Index rebuild required",
                    state.InitializationWarning);
                TryPersistEffectiveRecommendationFloor(
                    state.RecommendationFloor);
                TryPersistNetworkBoundary(state.NetworkBoundary);
                if (state.InitializationWarning is not null)
                {
                    LogSnapshotRejected(_logger, state.InitializationWarning);
                }

                Volatile.Write(ref _state, state);
                installed = true;
                return state;
            }
            finally
            {
                if (!installed)
                {
                    state.Dispose();
                }
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static async ValueTask<ISemanticDiscoveryRuntimeState> CreateStateAsync(
        SemanticDiscoveryPaths paths,
        JellyfinIndexSource source,
        JellyfinMediaAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        PluginConfiguration configuration =
            Plugin.Instance?.ReadSettings().ToConfiguration()
            ?? new PluginConfiguration();
        string modelDirectory = paths.GetModelDirectory(configuration);
        MiniLmEmbeddingGenerator? generator = null;
        HttpClient? markItDownClient = null;
        MarkItDownMcpReader? markItDown = null;
        RuntimeState? state = null;
        try
        {
            int ingestBatchSize = GetBoundedEnvironmentInt(
                "SEMANTIC_DISCOVERY_INGEST_BATCH_SIZE",
                8,
                32);
            int onnxIntraOpThreads = GetBoundedEnvironmentInt(
                "SEMANTIC_DISCOVERY_ONNX_INTRA_OP_THREADS",
                1,
                4);
            generator = new MiniLmEmbeddingGenerator(
                new MiniLmEmbeddingOptions
                {
                    ModelPath = Path.Combine(modelDirectory, "model.onnx"),
                    VocabularyPath = Path.Combine(modelDirectory, "vocab.txt"),
                    MaxBatchSize = ingestBatchSize,
                    MaxInputsPerRequest = ingestBatchSize,
                    IntraOpThreads = onnxIntraOpThreads,
                });
            InMemorySemanticVectorIndex vectorIndex = new();
            int maximumDocumentBytes = GetBoundedEnvironmentInt(
                "SEMANTIC_DISCOVERY_MAX_DOCUMENT_BYTES",
                8 * 1024 * 1024,
                MarkItDownMcpReader.AbsoluteMaximumDocumentBytes);
            int markItDownTimeoutSeconds = GetBoundedEnvironmentInt(
                "SEMANTIC_DISCOVERY_MARKITDOWN_TIMEOUT_SECONDS",
                120,
                900);
            int maximumRequestBytes = GetBoundedEnvironmentInt(
                "SEMANTIC_DISCOVERY_MAX_REQUEST_BYTES",
                12 * 1024 * 1024,
                MarkItDownMcpReader.AbsoluteMaximumRequestBytes);
            markItDownClient = MarkItDownMcpReader.CreateHttpClient(
                TimeSpan.FromSeconds(markItDownTimeoutSeconds));
            Uri markItDownEndpoint =
                SemanticDiscoveryPaths.GetMarkItDownEndpoint(configuration);
            NetworkBoundaryStatus networkBoundary =
                await VerifyNetworkBoundaryAsync(
                    markItDownClient,
                    markItDownEndpoint,
                    cancellationToken).ConfigureAwait(false);
            markItDown = new MarkItDownMcpReader(
                markItDownClient,
                markItDownEndpoint,
                new HeadingAwareChunker(generator),
                maximumDocumentBytes,
                maximumConcurrency: 1,
                maximumRequestBytes: maximumRequestBytes);
            SemanticIndexBuilder builder = new(
                generator,
                generator,
                source,
                [
                    new SubtitleContentReader(new SubtitleChunker(generator)),
                    markItDown,
                ],
                new AtomicSnapshotStore(paths.GetSnapshotPath(configuration)),
                vectorIndex,
                generator.Identity,
                embeddingBatchSize: ingestBatchSize);
            float recommendationFloor = GetBoundedEnvironmentFloat(
                "SEMANTIC_DISCOVERY_RECOMMENDATION_FLOOR",
                new SearchScoreCalibration().RecommendationFloor);
            SemanticDiscoveryService discovery = new(
                generator,
                vectorIndex,
                accessPolicy,
                new SearchScoreCalibration
                {
                    RecommendationFloor = recommendationFloor,
                });
            state = new RuntimeState(
                generator,
                markItDown,
                markItDownClient,
                builder,
                discovery,
                recommendationFloor,
                networkBoundary);
            state.HasIndex = await builder.TryLoadSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            return state;
        }
        catch (Exception primary)
        {
            try
            {
                if (state is not null)
                {
                    state.Dispose();
                }
                else
                {
                    markItDown?.Dispose();
                    markItDownClient?.Dispose();
                    generator?.Dispose();
                }
            }
            catch (Exception cleanup)
            {
                primary.Data["SemanticDiscoveryCleanupFailure"] =
                    cleanup.GetType().FullName;
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(primary)
                .Throw();
            throw;
        }
    }

    private void EnterOperation()
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposing || _disposed, this);
            _activeOperations++;
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifetimeLock)
        {
            _activeOperations--;
            if (_disposing && _activeOperations == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    private void TrySetStatus(string status, string? error = null)
    {
        _status = status;
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.UpdateRuntimeConfiguration(configuration =>
            {
                configuration.LastStatus = status;
                configuration.LastError = error ?? string.Empty;
                configuration.LastUpdatedUtc = DateTimeOffset.UtcNow;
            });
        }
        catch (Exception exception) when (IsConfigurationPersistenceFailure(exception))
        {
            LogStatusPersistenceFailure(_logger, exception, status);
        }
    }

    private void SetSuccessfulStatus(
        ISemanticDiscoveryRuntimeState state,
        IndexBuildResult result,
        string status,
        string? error)
    {
        _status = status;
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.UpdateRuntimeConfiguration(configuration =>
            {
                configuration.LastStatus = status;
                configuration.LastError = error ?? string.Empty;
                configuration.LastUpdatedUtc = DateTimeOffset.UtcNow;
                configuration.LastSuccessfulRefreshUtc = configuration.LastUpdatedUtc;
                configuration.IndexGeneration++;
                configuration.MetadataRecordCount = result.MetadataCount;
                configuration.ContentChunkCount = result.ContentChunkCount;
                configuration.LastEmbeddedMetadataCount =
                    result.EmbeddedMetadataCount;
                configuration.LastEmbeddedContentChunkCount =
                    result.EmbeddedContentChunkCount;
                configuration.IngestEmbeddingsTotal = checked(
                    configuration.IngestEmbeddingsTotal +
                    result.EmbeddedMetadataCount +
                    result.EmbeddedContentChunkCount);
                configuration.IndexIdentity = state.IndexIdentity;
                configuration.EffectiveRecommendationFloor =
                    state.RecommendationFloor;
                using Process process = Process.GetCurrentProcess();
                configuration.ClientProcessCurrentWorkingSetBytes =
                    Environment.WorkingSet;
                configuration.ClientProcessPeakWorkingSetBytes =
                    process.PeakWorkingSet64;
                if (state.LargestClientRequestMemory is { } requestMemory)
                {
                    configuration.LargestConversionSourceBytes =
                        requestMemory.SourceBytes;
                    configuration.LargestSerializedRequestBytes =
                        requestMemory.SerializedRequestBytes;
                    configuration.LargestRequestBeforeCurrentWorkingSetBytes =
                        requestMemory.BeforeCurrentWorkingSetBytes;
                    configuration.LargestRequestBeforePeakWorkingSetBytes =
                        requestMemory.BeforePeakWorkingSetBytes;
                    configuration.LargestRequestAfterCurrentWorkingSetBytes =
                        requestMemory.AfterCurrentWorkingSetBytes;
                    configuration.LargestRequestAfterPeakWorkingSetBytes =
                        requestMemory.AfterPeakWorkingSetBytes;
                }
                ApplyNetworkBoundary(
                    configuration,
                    state.NetworkBoundary);
                configuration.ModelStatus = "Pinned model and tokenizer validated";
            });
        }

        catch (Exception exception) when (IsConfigurationPersistenceFailure(exception))
        {
            _status = "Index published; status persistence failed";
            LogStatusPersistenceFailure(_logger, exception, _status);
            throw new IOException(
                "The semantic index was published, but plugin status could not be persisted.",
                exception);
        }
    }

    private void TryPersistEffectiveRecommendationFloor(float floor)
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.UpdateRuntimeConfiguration(configuration =>
            {
                configuration.EffectiveRecommendationFloor = floor;
            });
        }
        catch (Exception exception) when (IsConfigurationPersistenceFailure(exception))
        {
            LogStatusPersistenceFailure(
                _logger,
                exception,
                "effective recommendation floor");
        }
    }

    private void TryPersistNetworkBoundary(NetworkBoundaryStatus boundary)
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.UpdateRuntimeConfiguration(configuration =>
                ApplyNetworkBoundary(configuration, boundary));
        }
        catch (Exception exception) when (IsConfigurationPersistenceFailure(exception))
        {
            LogStatusPersistenceFailure(
                _logger,
                exception,
                "network boundary");
        }
    }

    private static void ApplyNetworkBoundary(
        PluginConfiguration configuration,
        NetworkBoundaryStatus boundary)
    {
        configuration.ExternalEgressCanaryBlocked =
            boundary.ExternalEgressCanaryBlocked;
        configuration.MarkItDownReachable =
            boundary.MarkItDownReachable;
        configuration.MarkItDownReachabilityProbe =
            boundary.MarkItDownReachabilityProbe;
        configuration.EgressEnforcement = boundary.Enforcement;
        configuration.EgressResidualLimitation =
            boundary.ResidualLimitation;
    }

    private static async Task<NetworkBoundaryStatus> VerifyNetworkBoundaryAsync(
        HttpClient markItDownClient,
        Uri markItDownEndpoint,
        CancellationToken cancellationToken)
    {
        await MarkItDownMcpReader.ProbeAsync(
            markItDownClient,
            markItDownEndpoint,
            cancellationToken).ConfigureAwait(false);

        string? canaryUrl = Environment.GetEnvironmentVariable(
            "SEMANTIC_DISCOVERY_EGRESS_CANARY_URL");
        if (string.IsNullOrWhiteSpace(canaryUrl))
        {
            return new NetworkBoundaryStatus(
                ExternalEgressCanaryBlocked: false,
                MarkItDownReachable: true,
                MarkItDownReachabilityProbe:
                    "JSON-RPC tools/list at the exact configured MCP endpoint",
                Enforcement: "not-configured",
                ResidualLimitation:
                    "No proxy canary was configured; direct socket egress was not tested.");
        }

        Uri canary = new(canaryUrl, UriKind.Absolute);
        if (canary.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "The egress canary must use HTTP or HTTPS.");
        }

        bool blocked;
        using (HttpClient external = new()
               {
                   Timeout = TimeSpan.FromSeconds(3),
               })
        {
            try
            {
                using HttpResponseMessage response =
                    await external.GetAsync(
                        canary,
                        cancellationToken).ConfigureAwait(false);
                blocked = false;
            }
            catch (HttpRequestException)
            {
                blocked = true;
            }
            catch (TaskCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                blocked = true;
            }
        }

        if (!blocked)
        {
            throw new InvalidOperationException(
                "The configured Jellyfin outbound HTTP canary was reachable.");
        }

        return new NetworkBoundaryStatus(
            ExternalEgressCanaryBlocked: true,
            MarkItDownReachable: true,
            MarkItDownReachabilityProbe:
                "JSON-RPC tools/list at the exact configured MCP endpoint",
            Enforcement:
                "black-hole HTTP_PROXY/HTTPS_PROXY/ALL_PROXY with NO_PROXY limited to the internal MarkItDown service",
            ResidualLimitation:
                "This constrains proxy-aware HTTP clients; the Aspire DCP container network still permits direct socket egress.");
    }

    private void TryPersistRecommendationDiagnostics(
        long queryCount,
        long resultCount,
        Guid sourceItemId,
        IReadOnlyList<SimilarItemResult> results,
        float recommendationFloor)
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.UpdateRuntimeConfiguration(configuration =>
            {
                configuration.RecommendationQueryCount = Math.Max(
                    configuration.RecommendationQueryCount,
                    queryCount);
                configuration.RecommendationResultCount = Math.Max(
                    configuration.RecommendationResultCount,
                    resultCount);
                configuration.LastRecommendationSourceItemId =
                    sourceItemId.ToString("N");
                configuration.LastRecommendationResultsJson =
                    System.Text.Json.JsonSerializer.Serialize(
                        results.Select(static result => new
                        {
                            itemId = result.ItemId.ToString("N"),
                            score = result.Score,
                        }));
                configuration.LastRecommendationEffectiveFloor =
                    recommendationFloor;
            });
        }
        catch (Exception exception) when (IsConfigurationPersistenceFailure(exception))
        {
            LogStatusPersistenceFailure(
                _logger,
                exception,
                "Recommendation query counter");
        }
    }

    private void TryPersistQueryEmbeddings(long queryEmbeddings)
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.UpdateRuntimeConfiguration(configuration =>
            {
                configuration.QueryEmbeddingsTotal = Math.Max(
                    configuration.QueryEmbeddingsTotal,
                    queryEmbeddings);
            });
        }
        catch (Exception exception) when (IsConfigurationPersistenceFailure(exception))
        {
            LogStatusPersistenceFailure(
                _logger,
                exception,
                "Query embedding counter");
        }
    }

    private static bool IsOperationalFailure(Exception exception) =>
        exception is IOException
            or HttpRequestException
            or InvalidDataException
            or OnnxRuntimeException
            or UnauthorizedAccessException;

    private static bool IsConfigurationPersistenceFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException;

    private static string GetSafeError(Exception exception) =>
        $"{exception.GetType().Name}: operation failed; see server logs.";

    private static bool WaitWithoutPropagating(Task task, TimeSpan timeout) =>
        ReferenceEquals(
            Task.WhenAny(task, Task.Delay(timeout)).GetAwaiter().GetResult(),
            task);

    private static int GetBoundedEnvironmentInt(
        string name,
        int fallback,
        int maximum)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsed) &&
            parsed is > 0 &&
            parsed <= maximum
                ? parsed
                : throw new InvalidDataException(
                    $"{name} must be within 1..{maximum}.");
    }

    internal static float GetBoundedEnvironmentFloat(
        string name,
        float fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return fallback;
        }

        return float.TryParse(
                value,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out float parsed) &&
            float.IsFinite(parsed) &&
            parsed is >= 0F and <= 1F
                ? parsed
                : throw new InvalidDataException(
                    $"{name} must be a finite value within [0, 1].");
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message =
            "Semantic index ready: {MetadataCount} items, {ContentCount} chunks; " +
            "embedded {EmbeddedMetadataCount} items and {EmbeddedContentCount} chunks; " +
            "{ErrorCount} ingestion errors")]
    private static partial void LogRebuildCompleted(
        ILogger logger,
        int metadataCount,
        int contentCount,
        int embeddedMetadataCount,
        int embeddedContentCount,
        int errorCount);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Error,
        Message = "Semantic discovery operation failed")]
    private static partial void LogRuntimeFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "{SnapshotWarning}")]
    private static partial void LogSnapshotRejected(
        ILogger logger,
        string snapshotWarning);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Could not persist Semantic Discovery status '{Status}'")]
    private static partial void LogStatusPersistenceFailure(
        ILogger logger,
        Exception exception,
        string status);

    private sealed class RuntimeState(
        MiniLmEmbeddingGenerator generator,
        MarkItDownMcpReader markItDown,
        HttpClient markItDownClient,
        SemanticIndexBuilder builder,
        SemanticDiscoveryService discovery,
        float recommendationFloor,
        NetworkBoundaryStatus networkBoundary)
        : ISemanticDiscoveryRuntimeState
    {
        private int _hasIndex;

        public bool HasIndex
        {
            get => Volatile.Read(ref _hasIndex) != 0;
            set => Volatile.Write(ref _hasIndex, value ? 1 : 0);
        }

        public string? InitializationWarning => builder.LastSnapshotLoadError;

        public string IndexIdentity => generator.Identity.CompositeHash;

        public float RecommendationFloor { get; } = recommendationFloor;

        public NetworkBoundaryStatus NetworkBoundary { get; } =
            networkBoundary;

        public ClientRequestMemoryObservation? LargestClientRequestMemory =>
            markItDown.LargestRequestMemoryObservation;

        public ValueTask<IReadOnlyList<SemanticSearchResult>> SearchAsync(
            string query,
            SearchScope scope,
            int limit,
            CancellationToken cancellationToken) =>
            discovery.SearchAsync(query, scope, limit, cancellationToken);

        public ValueTask<IReadOnlyList<SimilarItemResult>> GetSimilarAsync(
            Guid itemId,
            SearchScope scope,
            IReadOnlySet<Guid> excludedItemIds,
            int limit,
            CancellationToken cancellationToken) =>
            discovery.GetSimilarAsync(
                itemId,
                scope,
                excludedItemIds,
                limit,
                cancellationToken);

        public ValueTask<IndexBuildResult> RebuildAsync(
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            builder.RebuildAsync(progress, cancellationToken);

        public void Dispose()
        {
            List<Exception> failures = [];
            TryDispose(markItDown, failures);
            TryDispose(markItDownClient, failures);
            TryDispose(generator, failures);
            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failures[0])
                    .Throw();
            }
            if (failures.Count > 1)
            {
                throw new AggregateException(failures);
            }
        }

        private static void TryDispose(IDisposable value, List<Exception> failures)
        {
            try
            {
                value.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

    }
}

internal sealed record NetworkBoundaryStatus(
    bool ExternalEgressCanaryBlocked,
    bool MarkItDownReachable,
    string MarkItDownReachabilityProbe,
    string Enforcement,
    string ResidualLimitation);
