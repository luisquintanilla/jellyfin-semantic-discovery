using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.SemanticDiscovery.Extraction.Manifest;

public sealed record ExtractionManifest(
    int SchemaVersion,
    IReadOnlyList<ExtractionManifestItem> Items);

public sealed record ExtractionManifestItem(
    string Id,
    string SourcePath,
    string SourceSha256,
    long SourceSizeBytes,
    int PageCount);

public interface IExtractionManifestCatalog
{
    string ManifestRoot { get; }

    ValueTask<ExtractionManifestItem?> FindAsync(
        string itemId,
        CancellationToken cancellationToken);
}

public sealed class JsonExtractionManifestCatalog : IExtractionManifestCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly string _manifestPath;

    public JsonExtractionManifestCatalog(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        _manifestPath = Path.GetFullPath(manifestPath);
        ManifestRoot = Path.GetDirectoryName(_manifestPath)
            ?? throw new ArgumentException(
                "The manifest path must have a parent directory.",
                nameof(manifestPath));
    }

    public string ManifestRoot { get; }

    public async ValueTask<ExtractionManifestItem?> FindAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await using FileStream stream = new(
            _manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ExtractionManifest manifest =
            await JsonSerializer.DeserializeAsync<ExtractionManifest>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The extraction manifest is empty.");
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported extraction manifest schema version '{manifest.SchemaVersion}'.");
        }

        return manifest.Items.SingleOrDefault(
            item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
    }
}
