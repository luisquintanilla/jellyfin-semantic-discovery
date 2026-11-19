using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Setup;

public sealed record ExtractionBootstrapOptions
{
    public required Uri Endpoint { get; init; }

    public required string ManifestPath { get; init; }

    public string Profile { get; init; } = "Hybrid";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed class ExtractionBootstrapper(
    HttpClient httpClient,
    ExtractionBootstrapOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string manifestPath = Path.GetFullPath(options.ManifestPath);
        await using FileStream stream = new(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        BootstrapManifest manifest =
            await JsonSerializer.DeserializeAsync<BootstrapManifest>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The extraction manifest is empty.");
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "The extraction manifest schema version is unsupported.");
        }
        if (manifest.ProfileId == "demo-v1")
        {
            await ValidateDemoAssetsAsync(
                Path.GetDirectoryName(manifestPath)!,
                manifest.ArtworkStatus,
                manifest.Assets,
                cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        foreach (BootstrapManifestItem item in manifest.Items)
        {
            ValidateDeliveryReady(item);
            using HttpResponseMessage response =
                await httpClient.PostAsJsonAsync(
                    new Uri(options.Endpoint, "/extractions"),
                    new
                    {
                        manifestItemId = item.Id,
                        expectedSourceSha256 = item.SourceSha256,
                        extractionProfile = options.Profile,
                    },
                    timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            AcceptedExtraction accepted =
                await response.Content.ReadFromJsonAsync<AcceptedExtraction>(
                    JsonOptions,
                    timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Extraction '{item.Id}' returned an empty response.");
            await WaitForCompletionAsync(
                item.Id,
                accepted.StatusUri,
                timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task WaitForCompletionAsync(
        string itemId,
        string statusUri,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractionStatus status =
                await httpClient.GetFromJsonAsync<ExtractionStatus>(
                    new Uri(options.Endpoint, statusUri),
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Extraction '{itemId}' returned an empty status.");
            switch (status.State)
            {
                case "Completed":
                    return;
                case "Failed":
                case "Cancelled":
                    throw new InvalidDataException(
                        $"Extraction '{itemId}' ended as {status.State}: " +
                        $"{status.Error}");
                default:
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static void ValidateDeliveryReady(
        BootstrapManifestItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Id) ||
            item.SourceSha256.Length != 64 ||
            item.SourceSha256.Any(character => !Uri.IsHexDigit(character)) ||
            item.SourceSizeBytes <= 0 ||
            item.PageCount <= 0)
        {
            throw new InvalidDataException(
                $"Manifest item '{item.Id}' is still a delivery placeholder.");
        }
    }

    private static async Task ValidateDemoAssetsAsync(
        string manifestRoot,
        string? artworkStatus,
        BootstrapAsset[]? assets,
        CancellationToken cancellationToken)
    {
        if (assets is null ||
            assets.Length != 9 ||
            assets.Count(static asset => asset.MediaKind == "Movie") != 3 ||
            assets.Count(static asset => asset.MediaKind == "Audio") != 3 ||
            assets.Count(static asset => asset.MediaKind == "Book") != 3)
        {
            throw new InvalidDataException(
                "The demo manifest must contain exactly three Movie, " +
                "three Audio, and three Book assets.");
        }

        foreach (BootstrapAsset asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Media) ||
                string.IsNullOrWhiteSpace(asset.Sha256) ||
                asset.Sha256.Length != 64 ||
                asset.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
                asset.Bytes <= 0 ||
                string.IsNullOrWhiteSpace(asset.Authorship) ||
                string.IsNullOrWhiteSpace(asset.License) ||
                asset.Authorship.Contains(
                    "Pending",
                    StringComparison.OrdinalIgnoreCase) ||
                asset.License.Contains(
                    "Pending",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Demo asset '{asset.Id}' is still a delivery placeholder.");
            }

            string mediaPath = ResolveContainedPath(
                manifestRoot,
                asset.Media);
            FileInfo media = new(mediaPath);
            if (!media.Exists || media.Length != asset.Bytes)
            {
                throw new InvalidDataException(
                    $"Demo asset '{asset.Id}' has a missing or size-mismatched media file.");
            }

            await using FileStream stream = new(
                mediaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string actualHash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false));
            if (!string.Equals(
                    actualHash,
                    asset.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Demo asset '{asset.Id}' failed SHA-256 validation.");
            }

            await ValidateRelatedFileAsync(
                manifestRoot,
                asset.Metadata,
                asset.MetadataSha256,
                asset.Id,
                "metadata",
                cancellationToken).ConfigureAwait(false);
            if (asset.MediaKind is "Movie" or "Audio")
            {
                await ValidateRelatedFileAsync(
                    manifestRoot,
                    asset.Transcript,
                    asset.TranscriptSha256,
                    asset.Id,
                    "transcript",
                    cancellationToken).ConfigureAwait(false);
            }

            bool artworkRequired = string.Equals(
                artworkStatus,
                "ready",
                StringComparison.Ordinal);
            if (artworkRequired &&
                (string.IsNullOrWhiteSpace(asset.Artwork) ||
                 string.IsNullOrWhiteSpace(asset.ArtworkSha256)))
            {
                throw new InvalidDataException(
                    $"Demo asset '{asset.Id}' is missing approved artwork.");
            }

            if (!string.IsNullOrWhiteSpace(asset.Artwork))
            {
                await ValidateRelatedFileAsync(
                    manifestRoot,
                    asset.Artwork,
                    asset.ArtworkSha256,
                    asset.Id,
                    "artwork",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(asset.Backdrop))
            {
                await ValidateRelatedFileAsync(
                    manifestRoot,
                    asset.Backdrop,
                    asset.BackdropSha256,
                    asset.Id,
                    "backdrop",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ValidateRelatedFileAsync(
        string manifestRoot,
        string? relativePath,
        string? expectedSha256,
        string assetId,
        string role,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException(
                $"Demo asset '{assetId}' is missing its {role} file.");
        }

        string path = ResolveContainedPath(manifestRoot, relativePath);
        if (!File.Exists(path))
        {
            throw new InvalidDataException(
                $"Demo asset '{assetId}' is missing its {role} file.");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return;
        }

        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"Demo asset '{assetId}' has an invalid {role} SHA-256.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actualHash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false));
        if (!string.Equals(
                actualHash,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Demo asset '{assetId}' has a {role} SHA-256 mismatch.");
        }
    }

    private static string ResolveContainedPath(
        string manifestRoot,
        string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                "Demo asset paths must be relative.");
        }

        string root = Path.GetFullPath(manifestRoot);
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string relative = Path.GetRelativePath(root, path);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(
                "A demo asset path escaped the manifest root.");
        }

        return path;
    }

    private sealed record BootstrapManifest(
        int SchemaVersion,
        string? ProfileId,
        string? ArtworkStatus,
        BootstrapAsset[]? Assets,
        BootstrapManifestItem[] Items);

    private sealed record BootstrapAsset(
        string Id,
        string MediaKind,
        string? Media,
        string? Transcript,
        string? Metadata,
        string? Artwork,
        string? Sha256,
        string? TranscriptSha256,
        string? MetadataSha256,
        string? ArtworkSha256,
        string? Backdrop,
        string? BackdropSha256,
        long Bytes,
        string Authorship,
        string License);

    private sealed record BootstrapManifestItem(
        string Id,
        string SourceSha256,
        long SourceSizeBytes,
        int PageCount);

    private sealed record AcceptedExtraction(string StatusUri);

    private sealed record ExtractionStatus(
        string State,
        string? Error);
}
