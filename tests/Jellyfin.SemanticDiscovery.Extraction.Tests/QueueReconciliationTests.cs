using Jellyfin.SemanticDiscovery.Extraction.Artifacts;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Extraction;
using Jellyfin.SemanticDiscovery.Extraction.Jobs;
using Jellyfin.SemanticDiscovery.Extraction.Persistence;
using Jellyfin.SemanticDiscovery.Extraction.Validation;
using Jellyfin.SemanticDiscovery.Extraction.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

public sealed class QueueReconciliationTests
{
    [Fact]
    public async Task JobIdentityIncludesExactModel()
    {
        using TestDirectory directory = new();
        using FileExtractionJobRepository repository = new(
            directory.Path,
            new AtomicJsonFile(new AtomicFileOperations()));

        ExtractionJob eightB = await repository.GetOrCreateAsync(
            "book",
            new string('a', 64),
            ExtractionProfile.Hybrid,
            "qwen3-vl:8b-instruct",
            CancellationToken.None);
        ExtractionJob fourB = await repository.GetOrCreateAsync(
            "book",
            new string('a', 64),
            ExtractionProfile.Hybrid,
            "qwen3-vl:4b-instruct",
            CancellationToken.None);

        Assert.NotEqual(eightB.JobId, fourB.JobId);
        Assert.Equal("qwen3-vl:8b-instruct", eightB.Model);
        Assert.Equal("qwen3-vl:4b-instruct", fourB.Model);
    }

    [Fact]
    public async Task PendingJobCanBeClaimedOnlyOnce()
    {
        using TestDirectory directory = new();
        using FileExtractionJobRepository repository = new(
            directory.Path,
            new AtomicJsonFile(new AtomicFileOperations()));
        ExtractionJob pending = await repository.GetOrCreateAsync(
            "book",
            new string('a', 64),
            ExtractionProfile.Hybrid,
            "qwen3-vl:8b-instruct",
            CancellationToken.None);

        ExtractionJob?[] claims = await Task.WhenAll(
            repository.TryClaimPendingAsync(
                pending.JobId,
                CancellationToken.None).AsTask(),
            repository.TryClaimPendingAsync(
                pending.JobId,
                CancellationToken.None).AsTask());

        Assert.Single(claims, static claim => claim is not null);
    }

    [Fact]
    public async Task RestartRequeuesPendingAndRunningJobsAndFinalizesCancellation()
    {
        using TestDirectory directory = new();
        AtomicJsonFile atomicJson = new(new AtomicFileOperations());
        FileExtractionJobRepository repository = new(
            directory.Path,
            atomicJson);
        ExtractionJob pending = await repository.GetOrCreateAsync(
            "pending",
            new string('a', 64),
            ExtractionProfile.Text,
            "qwen3-vl:8b-instruct",
            CancellationToken.None);
        ExtractionJob running = await repository.GetOrCreateAsync(
            "running",
            new string('b', 64),
            ExtractionProfile.Hybrid,
            "qwen3-vl:8b-instruct",
            CancellationToken.None);
        await repository.UpdateAsync(
            running.JobId,
            job => job with { State = ExtractionJobState.Running },
            CancellationToken.None);
        ExtractionJob cancelling = await repository.GetOrCreateAsync(
            "cancelling",
            new string('c', 64),
            ExtractionProfile.Vision,
            "qwen3-vl:8b-instruct",
            CancellationToken.None);
        await repository.UpdateAsync(
            cancelling.JobId,
            job => job with
            {
                State = ExtractionJobState.CancellationRequested,
            },
            CancellationToken.None);
        BoundedExtractionJobChannel queue = new(8);
        ExtractionWorker worker = new(
            queue,
            repository,
            new ExtractionCancellationRegistry(),
            new UnusedValidator(),
            new UnusedPipeline(),
            new UnusedPublisher(),
            new ExtractionOptions { Concurrency = 1 },
            new ReadinessState(),
            NullLogger<ExtractionWorker>.Instance);

        await worker.ReconcileAsync(CancellationToken.None);

        string first = await queue.DequeueAsync(CancellationToken.None);
        string second = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(
            [pending.JobId, running.JobId],
            new[] { first, second });
        ExtractionJob reconciledRunning =
            await repository.FindAsync(
                running.JobId,
                CancellationToken.None)
            ?? throw new InvalidDataException("Running job was lost.");
        ExtractionJob reconciledCancellation =
            await repository.FindAsync(
                cancelling.JobId,
                CancellationToken.None)
            ?? throw new InvalidDataException("Cancelling job was lost.");
        Assert.Equal(ExtractionJobState.Pending, reconciledRunning.State);
        Assert.Equal(
            ExtractionJobState.Cancelled,
            reconciledCancellation.State);
    }

    private sealed class UnusedValidator : IExtractionSourceValidator
    {
        public ValueTask<ValidatedExtractionSource> ValidateAsync(
            string manifestItemId,
            string expectedSourceSha256,
            ExtractionProfile profile,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPipeline : IDocumentExtractionPipeline
    {
        public ValueTask<ExtractionArtifactBundle> ExtractAsync(
            ExtractionJob job,
            ValidatedExtractionSource source,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPublisher : ICompletedArtifactPublisher
    {
        public ValueTask<string> PublishAsync(
            ExtractionArtifactBundle artifact,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
