using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Validation;

namespace Jellyfin.SemanticDiscovery.Extraction.Jobs;

public interface IExtractionCoordinator
{
    ValueTask<ExtractionJob> SubmitAsync(
        CreateExtractionRequest request,
        CancellationToken cancellationToken);

    ValueTask<ExtractionJob?> FindAsync(
        string jobId,
        CancellationToken cancellationToken);

    ValueTask<ExtractionJob?> CancelAsync(
        string jobId,
        CancellationToken cancellationToken);
}

public sealed class ExtractionCoordinator(
    IExtractionSourceValidator sourceValidator,
    IExtractionJobRepository repository,
    IExtractionJobChannel queue,
    IExtractionCancellationRegistry cancellationRegistry,
    OllamaOptions ollamaOptions) :
    IExtractionCoordinator
{
    public async ValueTask<ExtractionJob> SubmitAsync(
        CreateExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatedExtractionSource source =
            await sourceValidator.ValidateAsync(
                request.ManifestItemId,
                request.ExpectedSourceSha256,
                request.ExtractionProfile,
                cancellationToken).ConfigureAwait(false);
        ExtractionJob job = await repository.GetOrCreateAsync(
            request.ManifestItemId,
            source.SourceSha256,
            request.ExtractionProfile,
            ollamaOptions.Model,
            cancellationToken).ConfigureAwait(false);
        if (job.State is ExtractionJobState.Failed or
            ExtractionJobState.Cancelled)
        {
            job = await repository.UpdateAsync(
                job.JobId,
                current => current with
                {
                    State = ExtractionJobState.Pending,
                    Error = null,
                    ArtifactPath = null,
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (job.State == ExtractionJobState.Pending)
        {
            await queue.EnqueueAsync(
                job.JobId,
                cancellationToken).ConfigureAwait(false);
        }

        return job;
    }

    public ValueTask<ExtractionJob?> FindAsync(
        string jobId,
        CancellationToken cancellationToken) =>
        repository.FindAsync(jobId, cancellationToken);

    public async ValueTask<ExtractionJob?> CancelAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        ExtractionJob? job = await repository.FindAsync(
            jobId,
            cancellationToken).ConfigureAwait(false);
        if (job is null || job.IsTerminal)
        {
            return job;
        }

        ExtractionJob updated = await repository.UpdateAsync(
            jobId,
            current => current.IsTerminal
                ? current
                : current with
                {
                    State = ExtractionJobState.CancellationRequested,
                },
            cancellationToken).ConfigureAwait(false);
        cancellationRegistry.Cancel(jobId);
        return updated;
    }
}
