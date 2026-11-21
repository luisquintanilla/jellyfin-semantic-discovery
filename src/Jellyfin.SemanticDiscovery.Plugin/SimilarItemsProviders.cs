using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.SemanticDiscovery.Plugin;

/// <summary>"More Like This": the nearest neighbors of an item, of the same kind, that the user may see.</summary>
public abstract class SimilarItemsProvider<T>(SemanticServiceClient service, ILibraryManager libraryManager)
    : ILocalSimilarItemsProvider<T>
    where T : BaseItem
{
    public string Name => "Semantic Discovery";

    public MetadataPluginType Type => MetadataPluginType.LocalSimilarityProvider;

    public async Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(T item, SimilarItemsQuery query, CancellationToken cancellationToken)
    {
        if (query.User is null)
        {
            return [];
        }

        int limit = Math.Clamp(query.Limit ?? 12, 1, 100);
        IReadOnlyList<SearchHit> hits = await service.SimilarAsync(item.Id, limit * 2, cancellationToken);
        return hits
            .Where(hit => !query.ExcludeItemIds.Contains(hit.Id))
            .Select(hit => libraryManager.GetItemById<BaseItem>(hit.Id, query.User))
            .OfType<T>()
            .Take(limit)
            .ToArray<BaseItem>();
    }
}

public sealed class MovieSimilarItemsProvider(SemanticServiceClient service, ILibraryManager libraryManager)
    : SimilarItemsProvider<Movie>(service, libraryManager);

public sealed class AudioSimilarItemsProvider(SemanticServiceClient service, ILibraryManager libraryManager)
    : SimilarItemsProvider<Audio>(service, libraryManager);

public sealed class BookSimilarItemsProvider(SemanticServiceClient service, ILibraryManager libraryManager)
    : SimilarItemsProvider<Book>(service, libraryManager);

public sealed class SeriesSimilarItemsProvider(SemanticServiceClient service, ILibraryManager libraryManager)
    : SimilarItemsProvider<Series>(service, libraryManager);
