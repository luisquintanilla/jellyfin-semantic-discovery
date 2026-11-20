using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.SemanticDiscovery.Plugin;

/// <summary>Adds semantic results to Jellyfin's search box, filtered to what the user may see.</summary>
public sealed class SemanticSearchProvider(SemanticServiceClient service, ILibraryManager libraryManager, IUserManager userManager)
    : IInternalSearchProvider
{
    public string Name => "Semantic Discovery";

    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    public int Priority => 50;

    public bool CanSearch(SearchProviderQuery query) =>
        !string.IsNullOrWhiteSpace(query?.SearchTerm) && query.UserId is { } id && id != Guid.Empty;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        if (query.UserId is not { } userId || userManager.GetUserById(userId) is not { } user)
        {
            return [];
        }

        IReadOnlyList<SearchHit> hits = await service.SearchAsync(query.SearchTerm, Math.Clamp(query.Limit ?? 20, 1, 100), cancellationToken);

        // GetItemById(id, user) returns null when the user can't see the item.
        return Visible(hits, id => libraryManager.GetItemById<BaseItem>(id, user)?.GetBaseItemKind(),
            query.IncludeItemTypes, query.ExcludeItemTypes);
    }

    /// <summary>Drops hits the user can't see (no kind) or didn't ask for.</summary>
    internal static SearchResult[] Visible(
        IEnumerable<SearchHit> hits, Func<Guid, BaseItemKind?> kindForUser, BaseItemKind[] include, BaseItemKind[] exclude) => hits
        .Where(hit => kindForUser(hit.Id) is { } kind
            && (include.Length == 0 || include.Contains(kind)) && !exclude.Contains(kind))
        // Jellyfin ranks search results on a 0-100 scale.
        .Select(hit => new SearchResult(hit.Id, (float)Math.Min(hit.Score * 100, 99)))
        .ToArray();
}
