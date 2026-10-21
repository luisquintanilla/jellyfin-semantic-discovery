using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class BookSimilarItemsProvider(
    SemanticDiscoveryRuntime runtime,
    ILibraryManager libraryManager)
    : ILocalSimilarItemsProvider<Book>
{
    private readonly SemanticDiscoveryRuntime _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));

    public string Name => "Semantic Discovery";

    public MetadataPluginType Type => MetadataPluginType.LocalSimilarityProvider;

    public async Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        Book item,
        SimilarItemsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(query);
        PluginSettings? settings = Plugin.Instance?.ReadSettings();
        if (settings?.Enabled != true || query.User is null)
        {
            return [];
        }

        IReadOnlyList<SimilarItemResult> results = await _runtime.GetSimilarAsync(
            item.Id,
            new SearchScope
            {
                UserId = query.User.Id,
                Kinds = new HashSet<MediaKind> { MediaKind.Book },
            },
            query.ExcludeItemIds.ToHashSet(),
            Math.Clamp(query.Limit ?? 20, 1, 100),
            cancellationToken).ConfigureAwait(false);
        return results
            .Select(result => _libraryManager.GetItemById<BaseItem>(
                result.ItemId,
                query.User))
            .OfType<Book>()
            .Cast<BaseItem>()
            .ToArray();
    }
}
