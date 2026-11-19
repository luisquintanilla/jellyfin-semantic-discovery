using System.Security.Cryptography;
using System.Text;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Persistence;

namespace Jellyfin.SemanticDiscovery.Extraction.Jobs;

public interface IExtractionJobRepository
{
    ValueTask<ExtractionJob> GetOrCreateAsync(
        string manifestItemId,
        string sourceSha256,
        ExtractionProfile profile,
        string model,
        CancellationToken cancellationToken);

    ValueTask<ExtractionJob?> FindAsync(
        string jobId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ExtractionJob>> ListIncompleteAsync(
        CancellationToken cancellationToken);

    ValueTask<ExtractionJob> UpdateAsync(
        string jobId,
        Func<ExtractionJob, ExtractionJob> update,
        CancellationToken cancellationToken);

    ValueTask<ExtractionJob?> TryClaimPendingAsync(
        string jobId,
        CancellationToken cancellationToken);
}

public sealed class FileExtractionJobRepository(
    string stateRoot,
    AtomicJsonFile atomicJsonFile) : IExtractionJobRepository, IDisposable
{
    private readonly string _jobsRoot = Path.Combine(
        Path.GetFullPath(
            string.IsNullOrWhiteSpace(stateRoot)
                ? throw new ArgumentException(
                    "A state root is required.",
                    nameof(stateRoot))
                : stateRoot),
        "jobs");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<ExtractionJob> GetOrCreateAsync(
        string manifestItemId,
        string sourceSha256,
        ExtractionProfile profile,
        string model,
        CancellationToken cancellationToken)
    {
        string jobId = CreateJobId(
            manifestItemId,
            sourceSha256,
            profile,
            model);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(jobId);
            if (File.Exists(path))
            {
                return await AtomicJsonFile.ReadAsync<ExtractionJob>(
                    path,
                    cancellationToken).ConfigureAwait(false);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            ExtractionJob job = new(
                jobId,
                manifestItemId,
                sourceSha256,
                profile,
                model,
                ExtractionJobState.Pending,
                now,
                now);
            await atomicJsonFile.WriteAsync(
                path,
                job,
                cancellationToken).ConfigureAwait(false);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ExtractionJob?> FindAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        string path = GetPath(jobId);
        return File.Exists(path)
            ? await AtomicJsonFile.ReadAsync<ExtractionJob>(
                path,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<IReadOnlyList<ExtractionJob>> ListIncompleteAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_jobsRoot))
        {
            return [];
        }

        List<ExtractionJob> jobs = [];
        foreach (string path in Directory.EnumerateFiles(
                     _jobsRoot,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractionJob job =
                await AtomicJsonFile.ReadAsync<ExtractionJob>(
                    path,
                    cancellationToken).ConfigureAwait(false);
            if (!job.IsTerminal)
            {
                jobs.Add(job);
            }
        }

        return jobs;
    }

    public async ValueTask<ExtractionJob> UpdateAsync(
        string jobId,
        Func<ExtractionJob, ExtractionJob> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(jobId);
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException(
                    $"Extraction job '{jobId}' was not found.");
            }

            ExtractionJob current =
                await AtomicJsonFile.ReadAsync<ExtractionJob>(
                    path,
                    cancellationToken).ConfigureAwait(false);
            ExtractionJob updated = update(current) with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await atomicJsonFile.WriteAsync(
                path,
                updated,
                cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ExtractionJob?> TryClaimPendingAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(jobId);
            if (!File.Exists(path))
            {
                return null;
            }

            ExtractionJob current =
                await AtomicJsonFile.ReadAsync<ExtractionJob>(
                    path,
                    cancellationToken).ConfigureAwait(false);
            if (current.State != ExtractionJobState.Pending)
            {
                return null;
            }

            ExtractionJob claimed = current with
            {
                State = ExtractionJobState.Running,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await atomicJsonFile.WriteAsync(
                path,
                claimed,
                cancellationToken).ConfigureAwait(false);
            return claimed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CreateJobId(
        string manifestItemId,
        string sourceSha256,
        ExtractionProfile profile,
        string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{manifestItemId}\n{sourceSha256}\n{profile}\n{model}"));
        return Convert.ToHexStringLower(hash)[..24];
    }

    private string GetPath(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) ||
            jobId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The job ID is invalid.", nameof(jobId));
        }

        return Path.Combine(_jobsRoot, $"{jobId}.json");
    }

    public void Dispose() => _gate.Dispose();
}
