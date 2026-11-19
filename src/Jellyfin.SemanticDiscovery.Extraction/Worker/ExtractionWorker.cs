using Jellyfin.SemanticDiscovery.Extraction.Artifacts;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Extraction;
using Jellyfin.SemanticDiscovery.Extraction.Jobs;
using Jellyfin.SemanticDiscovery.Extraction.Validation;

namespace Jellyfin.SemanticDiscovery.Extraction.Worker;

public interface IReadinessState
{
    bool IsReady { get; }

    string? Failure { get; }
}

public sealed class ReadinessState : IReadinessState
{
    private volatile bool _isReady;
    private string? _failure;

    public bool IsReady => _isReady;

    public string? Failure => _failure;

    public void Ready()
    {
        _failure = null;
        _isReady = true;
    }

    public void Failed(Exception exception)
    {
        _failure = exception.Message;
        _isReady = false;
    }
}

public sealed partial class ExtractionWorker(
    IExtractionJobChannel queue,
    IExtractionJobRepository repository,
    IExtractionCancellationRegistry cancellationRegistry,
    IExtractionSourceValidator sourceValidator,
    IDocumentExtractionPipeline pipeline,
    ICompletedArtifactPublisher artifactPublisher,
    ExtractionOptions options,
    ReadinessState readiness,
    ILogger<ExtractionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] consumers = Enumerable.Range(0, options.Concurrency)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();
        try
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            readiness.Ready();
            await Task.WhenAll(consumers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            readiness.Failed(exception);
            LogWorkerStopped(logger, exception);
            throw;
        }
    }

    public async ValueTask ReconcileAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExtractionJob> jobs =
            await repository.ListIncompleteAsync(
                cancellationToken).ConfigureAwait(false);
        foreach (ExtractionJob job in jobs.OrderBy(
                     candidate => candidate.CreatedAt))
        {
            if (job.State == ExtractionJobState.CancellationRequested)
            {
                await repository.UpdateAsync(
                    job.JobId,
                    current => current with
                    {
                        State = ExtractionJobState.Cancelled,
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (job.State == ExtractionJobState.Running)
            {
                await repository.UpdateAsync(
                    job.JobId,
                    current => current with
                    {
                        State = ExtractionJobState.Pending,
                        Error = null,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            await queue.EnqueueAsync(
                job.JobId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string jobId = await queue.DequeueAsync(
                stoppingToken).ConfigureAwait(false);
            await ProcessAsync(jobId, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ProcessAsync(
        string jobId,
        CancellationToken stoppingToken)
    {
        ExtractionJob? job = await repository.FindAsync(
            jobId,
            stoppingToken).ConfigureAwait(false);
        if (job is null || job.IsTerminal ||
            job.State == ExtractionJobState.Running)
        {
            return;
        }

        if (job.State == ExtractionJobState.CancellationRequested)
        {
            await MarkCancelledAsync(jobId).ConfigureAwait(false);
            return;
        }

        job = await repository.TryClaimPendingAsync(
            jobId,
            stoppingToken).ConfigureAwait(false);
        if (job is null)
        {
            ExtractionJob? latest = await repository.FindAsync(
                jobId,
                stoppingToken).ConfigureAwait(false);
            if (latest?.State == ExtractionJobState.CancellationRequested)
            {
                await MarkCancelledAsync(jobId).ConfigureAwait(false);
            }

            return;
        }

        CancellationToken jobToken = default;
        try
        {
            jobToken = cancellationRegistry.Register(
                jobId,
                stoppingToken);
            ExtractionJob? registeredJob = await repository.FindAsync(
                jobId,
                jobToken).ConfigureAwait(false);
            if (registeredJob?.State ==
                ExtractionJobState.CancellationRequested)
            {
                cancellationRegistry.Cancel(jobId);
                jobToken.ThrowIfCancellationRequested();
            }

            ValidatedExtractionSource source =
                await sourceValidator.ValidateAsync(
                    job.ManifestItemId,
                    job.SourceSha256,
                    job.ExtractionProfile,
                    jobToken).ConfigureAwait(false);
            ExtractionArtifactBundle artifact = await pipeline.ExtractAsync(
                job,
                source,
                jobToken).ConfigureAwait(false);
            string artifactPath = await artifactPublisher.PublishAsync(
                artifact,
                jobToken).ConfigureAwait(false);
            jobToken.ThrowIfCancellationRequested();
            ExtractionJob completed = await repository.UpdateAsync(
                jobId,
                current => current.State ==
                    ExtractionJobState.CancellationRequested
                    ? current
                    : current with
                    {
                        State = ExtractionJobState.Completed,
                        Error = null,
                        ArtifactPath = artifactPath,
                    },
                CancellationToken.None).ConfigureAwait(false);
            if (completed.State ==
                ExtractionJobState.CancellationRequested)
            {
                await MarkCancelledAsync(jobId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await repository.UpdateAsync(
                jobId,
                current => current with
                {
                    State = ExtractionJobState.Pending,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkCancelledAsync(jobId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogJobFailed(logger, exception, jobId);
            await repository.UpdateAsync(
                jobId,
                current => current with
                {
                    State = ExtractionJobState.Failed,
                    Error = exception.Message,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistry.Complete(jobId);
        }
    }

    private ValueTask<ExtractionJob> MarkCancelledAsync(string jobId) =>
        repository.UpdateAsync(
            jobId,
            current => current with
            {
                State = ExtractionJobState.Cancelled,
                Error = null,
            },
            CancellationToken.None);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Critical,
        Message = "The extraction worker stopped unexpectedly.")]
    private static partial void LogWorkerStopped(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Extraction job {JobId} failed.")]
    private static partial void LogJobFailed(
        ILogger logger,
        Exception exception,
        string jobId);
}
