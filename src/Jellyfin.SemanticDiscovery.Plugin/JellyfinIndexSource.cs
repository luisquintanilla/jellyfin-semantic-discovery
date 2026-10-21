using System.Runtime.CompilerServices;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Projection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class JellyfinIndexSource(ILibraryManager libraryManager) : IIndexSource
{
    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly BaseItemKind[] IndexedKinds =
        [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Book, BaseItemKind.Episode];
    private static readonly HashSet<string> SubtitleModifiers = new(
        [
            "default",
            "forced",
            "foreign",
            "sdh",
            "cc",
            "hi",
            "transcript",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));

    public async IAsyncEnumerable<IndexSourceItem> GetItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = IndexedKinds,
                Recursive = true,
                IsVirtualItem = false,
            });

        Dictionary<Guid, List<ContentSourceDescriptor>> contentByOwner = [];
        foreach (BaseItem item in items)
        {
            Guid? ownerId = item switch
            {
                Episode episode when episode.SeriesId != Guid.Empty => episode.SeriesId,
                Movie => item.Id,
                Book => item.Id,
                _ => null,
            };
            if (!ownerId.HasValue)
            {
                continue;
            }

            if (!contentByOwner.TryGetValue(
                    ownerId.Value,
                    out List<ContentSourceDescriptor>? content))
            {
                content = [];
                contentByOwner.Add(ownerId.Value, content);
            }

            content.AddRange(DiscoverContent(item, MapKind(item)));
        }

        foreach (BaseItem item in items
                     .Where(static item => item is Movie or Series or Book)
                     .OrderBy(static item => item.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaKind? kind = MapKind(item);
            if (!kind.HasValue)
            {
                continue;
            }

            yield return new IndexSourceItem
            {
                Metadata = CreateMetadata(item, kind.Value),
                ContentSources = contentByOwner.TryGetValue(
                    item.Id,
                    out List<ContentSourceDescriptor>? content)
                    ? content
                    : [],
            };

            await Task.Yield();
        }
    }

    private static MediaKind? MapKind(BaseItem item) => item switch
    {
        Movie => MediaKind.Movie,
        Series => MediaKind.Series,
        Book => MediaKind.Book,
        _ => null,
    };

    private static string[] NormalizeValues(IEnumerable<string> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private MetadataRecord CreateMetadata(BaseItem item, MediaKind kind)
    {
        MetadataRecord metadata = new()
        {
            Id = item.Id,
            Kind = kind,
            ItemType = item.GetType().Name,
            MediaType = item.MediaType.ToString(),
            Title = item.Name,
            OriginalTitle = item.OriginalTitle,
            Overview = item.Overview,
            ProductionYear = item.ProductionYear,
            LibraryId = item.GetTopParent()?.Id ?? item.ParentId,
            ParentId = item.ParentId,
            AncestorIds = item.GetAncestorIds()
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .ToArray(),
            Path = item.Path,
            Genres = NormalizeValues(item.Genres),
            Tags = NormalizeValues(item.Tags),
            People = _libraryManager
                .GetPeople(item)
                .Select(static person => person.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Studios = NormalizeValues(item.Studios),
            OfficialRating = item.OfficialRating,
        };
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return metadata;
        }

        string? directory = Path.GetDirectoryName(item.Path);
        if (directory is null)
        {
            return metadata;
        }
        string sidecarPath = Path.Combine(
            directory,
            "semantic-discovery.metadata.json");
        FileInfo sidecarFile = new(sidecarPath);
        if (!sidecarFile.Exists)
        {
            return metadata;
        }
        if ((sidecarFile.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Semantic metadata sidecar for '{item.Id}' is unsafe.");
        }

        using FileStream stream = new(
            sidecarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 64 * 1024)
        {
            throw new InvalidDataException(
                $"Semantic metadata sidecar for '{item.Id}' exceeds its bound.");
        }
        byte[] sidecarBytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(sidecarBytes);
        SemanticMetadataSidecar sidecar =
            JsonSerializer.Deserialize<SemanticMetadataSidecar>(
                sidecarBytes,
                SidecarJsonOptions)
            ?? throw new InvalidDataException(
                $"Semantic metadata sidecar for '{item.Id}' is empty.");
        MetadataRecord locked = metadata with
        {
            Kind = sidecar.Kind,
            ItemType = sidecar.ItemType,
            MediaType = sidecar.MediaType,
            Title = sidecar.Title,
            OriginalTitle = null,
            Overview = sidecar.Overview,
            ProductionYear = sidecar.ProductionYear,
            Genres = [.. sidecar.Genres],
            Tags = [.. sidecar.Tags],
            People = [.. sidecar.People],
            Studios = [.. sidecar.Studios],
        };
        if (sidecar.SchemaVersion != 2 ||
            sidecar.ProjectionHashContract !=
                MetadataProjectionIdentity.ContractId ||
            sidecar.Kind != kind ||
            sidecar.ItemType != item.GetType().Name ||
            sidecar.Title != item.Name ||
            !string.Equals(
                MetadataProjectionIdentity.ComputeCanonicalSha256(locked),
                sidecar.ProjectedMetadataSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Semantic metadata sidecar for '{item.Id}' failed identity validation.");
        }

        return locked;
    }

    internal static ContentSourceDescriptor[] DiscoverContent(
        BaseItem item,
        MediaKind? kind)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return [];
        }

        if (kind == MediaKind.Book)
        {
            string extension = Path.GetExtension(item.Path);
            ContentSourceKind? sourceKind = extension.ToLowerInvariant() switch
            {
                ".pdf" => ContentSourceKind.Pdf,
                ".epub" => ContentSourceKind.Epub,
                _ => null,
            };

            return sourceKind.HasValue
                ?
                [
                    new ContentSourceDescriptor
                    {
                        OwnerId = item.Id,
                        Kind = sourceKind.Value,
                        Path = item.Path,
                    },
                ]
                : [];
        }

        string? directory = Path.GetDirectoryName(item.Path);
        string stem = Path.GetFileNameWithoutExtension(item.Path);
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsMatchingSidecar(path, stem))
            .GroupBy(
                static path => Path.GetFileNameWithoutExtension(path),
                StringComparer.OrdinalIgnoreCase)
            .Select(static paths => paths
                .OrderBy(path =>
                    Path.GetExtension(path).Equals(
                        ".srt",
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .First())
            .Order(StringComparer.Ordinal)
            .Select(path => new ContentSourceDescriptor
            {
                OwnerId = item is Episode episode ? episode.SeriesId : item.Id,
                Kind = GetSuffixTokens(path, stem)
                    .Contains("transcript", StringComparer.OrdinalIgnoreCase)
                    ? ContentSourceKind.Transcript
                    : ContentSourceKind.Subtitle,
                Path = path,
                Language = InferLanguage(path, stem),
            })
            .ToArray();
    }

    private static string? InferLanguage(string path, string stem)
    {
        return GetSuffixTokens(path, stem)
            .FirstOrDefault(static token => IsLanguageTag(token));
    }

    private static bool IsMatchingSidecar(string path, string mediaStem)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string sidecarStem = Path.GetFileNameWithoutExtension(path);
        return string.Equals(sidecarStem, mediaStem, StringComparison.OrdinalIgnoreCase) ||
            sidecarStem.StartsWith($"{mediaStem}.", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetSuffixTokens(string path, string mediaStem)
    {
        string sidecarStem = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(sidecarStem, mediaStem, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return sidecarStem[(mediaStem.Length + 1)..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsLanguageTag(string token)
    {
        if (SubtitleModifiers.Contains(token))
        {
            return false;
        }

        string[] subtags = token.Split('-');
        return subtags[0].Length is >= 2 and <= 3 &&
            subtags[0].All(char.IsAsciiLetter) &&
            subtags.Skip(1).All(static subtag =>
                subtag.Length is >= 2 and <= 8 &&
                subtag.All(char.IsAsciiLetterOrDigit));
    }
}
