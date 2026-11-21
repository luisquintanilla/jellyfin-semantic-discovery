using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Jellyfin.SemanticDiscovery.SemanticService.Search;

/// <summary>A search result: a Jellyfin item id, and for books, the page that matched.</summary>
public sealed record SearchHit(Guid Id, double Score, int? Page, string? Snippet);

/// <summary>Semantic search over items and book pages, plus "More Like This".</summary>
public sealed class SemanticSearch(
    MediaIndex media,
    BookPages pages,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IConfiguration configuration)
{
    /// <summary>embeddinggemma returns 768-dimensional vectors.</summary>
    public const int Dimensions = 768;

    private readonly double _minScore = configuration.GetValue("Search:MinScore", 0.35);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<float> vector = await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken);

        List<SearchHit> hits = await media.Collection.CollectionExistsAsync(cancellationToken)
            ? await media.Collection.SearchAsync(vector, limit, cancellationToken: cancellationToken)
                .Select(r => new SearchHit(r.Record.Id, r.Score ?? 0, null, null))
                .ToListAsync(cancellationToken)
            : [];
        hits.AddRange(await pages.SearchAsync(vector, limit * 3, cancellationToken));

        return Merge(hits, _minScore, limit);
    }

    public async Task<IReadOnlyList<SearchHit>> SimilarAsync(Guid id, int limit, CancellationToken cancellationToken)
    {
        if (!await media.Collection.CollectionExistsAsync(cancellationToken)
            || await media.Collection.GetAsync(id, new() { IncludeVectors = true }, cancellationToken) is not { } item)
        {
            return [];
        }

        // The item's own vector is the query. It matches itself best, so drop it.
        return await media.Collection.SearchAsync(item.Vector, limit + 1, cancellationToken: cancellationToken)
            .Where(r => r.Record.Id != id)
            .Select(r => new SearchHit(r.Record.Id, r.Score ?? 0, null, null))
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Keep the best hit per item (an item can match on its metadata and on several pages).</summary>
    internal static IReadOnlyList<SearchHit> Merge(IEnumerable<SearchHit> hits, double minScore, int limit) => hits
        .Where(hit => hit.Score >= minScore)
        .GroupBy(hit => hit.Id)
        .Select(group => group.MaxBy(hit => hit.Score)!)
        .OrderByDescending(hit => hit.Score)
        .Take(limit)
        .ToList();
}
