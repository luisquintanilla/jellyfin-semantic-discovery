using Jellyfin.Data.Enums;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class SemanticSearchProvider(SemanticDiscoveryRuntime runtime)
    : IInternalSearchProvider
{
    internal const int MaximumQueryUtf16Length = 4096;
    internal const int MaximumQueryScalars = 1024;

    private readonly SemanticDiscoveryRuntime _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    public string Name => "Semantic Discovery";

    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    public int Priority => 50;

    public bool CanSearch(SearchProviderQuery query)
    {
        PluginSettings? settings = Plugin.Instance?.ReadSettings();
        return query is not null &&
            IsQuerySupported(query.SearchTerm) &&
            query.UserId is { } userId &&
            userId != Guid.Empty &&
            settings?.Enabled == true;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchProviderQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsQuerySupported(query.SearchTerm))
        {
            return [];
        }

        PluginSettings? settings = Plugin.Instance?.ReadSettings();
        if (settings?.Enabled != true ||
            query.UserId is not { } userId ||
            userId == Guid.Empty)
        {
            return [];
        }

        HashSet<string> includeTypes = query.IncludeItemTypes
            .Select(static kind => kind.ToString())
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> excludeTypes = query.ExcludeItemTypes
            .Select(static kind => kind.ToString())
            .ToHashSet(StringComparer.Ordinal);
        HashSet<MediaKind> kinds = GetKinds(query.IncludeItemTypes, query.ExcludeItemTypes);
        if (kinds.Count == 0)
        {
            return [];
        }

        SearchScope scope = new()
        {
            UserId = userId,
            Kinds = kinds,
            IncludeItemTypes = includeTypes,
            ExcludeItemTypes = excludeTypes,
            MediaTypes = query.MediaTypes
                .Select(static mediaType => mediaType.ToString())
                .ToHashSet(StringComparer.Ordinal),
            ParentId = query.ParentId is { } parentId && parentId != Guid.Empty
                ? parentId
                : null,
        };
        int limit = ResolveSearchLimit(
            query.Limit,
            settings.SearchLimit);
        IReadOnlyList<SemanticSearchResult> results =
            await _runtime.SearchAsync(
                query.SearchTerm,
                scope,
                limit,
                cancellationToken).ConfigureAwait(false);

        return results
            .Select(static result => new SearchResult(
                result.ItemId,
                result.Score >= 1F ? 99F : result.Score * 49F))
            .ToArray();
    }

    internal static int ResolveSearchLimit(
        int? requestedLimit,
        int configuredMaximum)
    {
        int maximum = Math.Clamp(configuredMaximum, 1, 100);
        int requested = Math.Clamp(requestedLimit ?? maximum, 1, 100);
        return Math.Min(requested, maximum);
    }

    internal static bool IsQuerySupported(string? query) =>
        !string.IsNullOrWhiteSpace(query) &&
        query.Length <= MaximumQueryUtf16Length &&
        query.EnumerateRunes()
            .Take(MaximumQueryScalars + 1)
            .Count() <= MaximumQueryScalars;

    private static HashSet<MediaKind> GetKinds(
        BaseItemKind[] included,
        BaseItemKind[] excluded)
    {
        Dictionary<BaseItemKind, MediaKind> supported = new()
        {
            [BaseItemKind.Movie] = MediaKind.Movie,
            [BaseItemKind.Series] = MediaKind.Series,
            [BaseItemKind.Book] = MediaKind.Book,
        };

        IEnumerable<BaseItemKind> requested = included.Length > 0
            ? included.Except(excluded)
            : supported.Keys.Except(excluded);
        return requested
            .Where(supported.ContainsKey)
            .Select(kind => supported[kind])
            .ToHashSet();
    }
}
