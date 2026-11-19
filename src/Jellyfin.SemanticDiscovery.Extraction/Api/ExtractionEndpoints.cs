using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Jobs;
using Jellyfin.SemanticDiscovery.Extraction.Validation;
using Jellyfin.SemanticDiscovery.Extraction.Worker;

namespace Jellyfin.SemanticDiscovery.Extraction.Api;

public static class ExtractionEndpoints
{
    public static IEndpointRouteBuilder MapExtractionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/extractions",
            async (
                CreateExtractionRequest request,
                IExtractionCoordinator coordinator,
                IReadinessState readiness,
                IExtractionJobChannel queue,
                CancellationToken cancellationToken) =>
            {
                if (!readiness.IsReady)
                {
                    return Results.Problem(
                        readiness.Failure ?? "Extraction service is not ready.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    ExtractionJob job = await coordinator.SubmitAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                    Uri statusUri = CreateStatusUri(job.JobId);
                    return Results.Accepted(
                        statusUri.ToString(),
                        new ExtractionAcceptedResponse(
                            job.JobId,
                            statusUri,
                            queue.IsDurable,
                            "The bounded Channel<T> queue is in-memory and is not durable. Persisted pending/running job state is reconciled after restart."));
                }
                catch (ExtractionValidationException exception)
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["request"] = [exception.Message],
                        });
                }
            });

        endpoints.MapGet(
            "/extractions/{jobId}",
            async (
                string jobId,
                IExtractionCoordinator coordinator,
                IExtractionJobChannel queue,
                CancellationToken cancellationToken) =>
            {
                ExtractionJob? job = await coordinator.FindAsync(
                    jobId,
                    cancellationToken).ConfigureAwait(false);
                return job is null
                    ? Results.NotFound()
                    : Results.Ok(ToResponse(job, queue));
            });

        endpoints.MapDelete(
            "/extractions/{jobId}",
            async (
                string jobId,
                IExtractionCoordinator coordinator,
                CancellationToken cancellationToken) =>
            {
                ExtractionJob? job = await coordinator.CancelAsync(
                    jobId,
                    cancellationToken).ConfigureAwait(false);
                if (job is null)
                {
                    return Results.NotFound();
                }

                Uri statusUri = CreateStatusUri(job.JobId);
                return Results.Accepted(
                    statusUri.ToString(),
                    new ExtractionCancellationResponse(
                        job.JobId,
                        job.State,
                        statusUri));
            });

        endpoints.MapGet(
            "/health/live",
            () => Results.Ok(new { status = "live" }));
        endpoints.MapGet(
            "/health/ready",
            (IReadinessState readiness) => readiness.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.Problem(
                    readiness.Failure ?? "Startup reconciliation is incomplete.",
                    statusCode: StatusCodes.Status503ServiceUnavailable));
        return endpoints;
    }

    private static ExtractionStatusResponse ToResponse(
        ExtractionJob job,
        IExtractionJobChannel queue) =>
        new(
            job.JobId,
            job.ManifestItemId,
            job.SourceSha256,
            job.ExtractionProfile,
            job.Model,
            job.State,
            job.CreatedAt,
            job.UpdatedAt,
            CreateStatusUri(job.JobId),
            queue.IsDurable,
            "The bounded Channel<T> queue is in-memory and is not durable. Persisted pending/running job state is reconciled after restart.",
            job.Error,
            job.ArtifactPath is null
                ? null
                : new Uri(job.ArtifactPath).AbsoluteUri);

    private static Uri CreateStatusUri(string jobId) =>
        new($"/extractions/{Uri.EscapeDataString(jobId)}", UriKind.Relative);
}
