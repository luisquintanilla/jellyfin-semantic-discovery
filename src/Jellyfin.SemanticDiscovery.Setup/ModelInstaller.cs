using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Setup;

public sealed class ModelInstaller(HttpClient httpClient)
{
    private const string HuggingFaceBaseUrl = "https://huggingface.co";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public static async ValueTask<ModelManifest> LoadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        ModelManifest manifest = await JsonSerializer.DeserializeAsync<ModelManifest>(
                   stream,
                   SerializerOptions,
                   cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The model manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public async Task InstallAsync(
        ModelManifest manifest,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        manifest.Validate();

        Directory.CreateDirectory(destinationDirectory);
        foreach (ModelFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destinationPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, file.Path));
            EnsureInside(destinationDirectory, destinationPath);

            if (await IsValidAsync(destinationPath, file, cancellationToken)
                    .ConfigureAwait(false))
            {
                Console.WriteLine($"verified {file.Path}");
                continue;
            }

            string? directory = Path.GetDirectoryName(destinationPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Uri uri = new(
                    $"{HuggingFaceBaseUrl}/{manifest.Repository}/resolve/" +
                    $"{manifest.Revision}/{file.SourcePath}");
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                request.Headers.UserAgent.Add(
                    new ProductInfoHeaderValue("jellyfin-semantic-discovery", "1.0"));
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                ValidateDeclaredContentLength(
                    response.Content.Headers.ContentLength,
                    file.Size,
                    file.Path);

                await using Stream source =
                    await response.Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                await using (FileStream target = new(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await CopyBoundedAsync(
                        source,
                        target,
                        file.Size,
                        cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    target.Flush(flushToDisk: true);
                }

                if (!await IsValidAsync(temporaryPath, file, cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        $"Downloaded model asset '{file.Path}' failed checksum or size validation.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, destinationPath, overwrite: true);
                Console.WriteLine($"installed {file.Path}");
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static void ValidateDeclaredContentLength(
        long? declaredLength,
        long expectedSize,
        string path)
    {
        if (declaredLength.HasValue && declaredLength.Value != expectedSize)
        {
            throw new InvalidDataException(
                $"Downloaded model asset '{path}' declared an unexpected size.");
        }
    }

    internal static async ValueTask CopyBoundedAsync(
        Stream source,
        Stream destination,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSize);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int maximumRead = checked((int)Math.Min(
                buffer.Length,
                (expectedSize - total) + 1));
            int read = await source.ReadAsync(
                buffer.AsMemory(0, maximumRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (total + read > expectedSize)
            {
                throw new InvalidDataException(
                    "Downloaded model asset exceeded its pinned size.");
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
            total += read;
        }

        if (total != expectedSize)
        {
            throw new InvalidDataException(
                "Downloaded model asset was shorter than its pinned size.");
        }
    }

    private static async ValueTask<bool> IsValidAsync(
        string path,
        ModelFile expected,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != expected.Size)
        {
            return false;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash),
            expected.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureInside(string directory, string path)
    {
        string root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root, comparison))
        {
            throw new InvalidDataException("A model manifest path escaped its destination.");
        }
    }
}
