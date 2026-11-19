using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.SemanticDiscovery.Extraction.Persistence;

public interface IAtomicFileOperations
{
    void Replace(string temporaryPath, string destinationPath);

    void Delete(string path);
}

public sealed class AtomicFileOperations : IAtomicFileOperations
{
    public void Replace(string temporaryPath, string destinationPath) =>
        File.Move(temporaryPath, destinationPath, overwrite: true);

    public void Delete(string path) => File.Delete(path);
}

public sealed class AtomicJsonFile(IAtomicFileOperations fileOperations)
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
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
                    value,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            fileOperations.Replace(temporaryPath, fullPath);
        }
        finally
        {
            fileOperations.Delete(temporaryPath);
        }
    }

    public static async ValueTask<T> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The JSON file '{path}' is empty.");
    }
}
