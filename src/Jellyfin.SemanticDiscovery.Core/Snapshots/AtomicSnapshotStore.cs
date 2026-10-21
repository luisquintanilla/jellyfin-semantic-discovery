using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Snapshots;

public interface IIndexSnapshotStore
{
    ValueTask<IndexSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        IndexSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public sealed class AtomicSnapshotStore(string path) : IIndexSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A snapshot path is required.", nameof(path))
            : path);

    public async ValueTask<IndexSnapshot?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        IndexSnapshot snapshot =
            await JsonSerializer.DeserializeAsync<IndexSnapshot>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The semantic index snapshot is empty.");

        IndexSnapshotValidator.Validate(snapshot);
        return snapshot;
    }

    public async ValueTask SaveAsync(
        IndexSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IndexSnapshotValidator.Validate(snapshot);

        string? directory = Path.GetDirectoryName(_path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
