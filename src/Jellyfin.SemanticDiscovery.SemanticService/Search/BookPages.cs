using Microsoft.Extensions.VectorData;

namespace Jellyfin.SemanticDiscovery.SemanticService.Search;

/// <summary>
/// A typed view of the chunks that the ingestion pipeline's <c>VectorStoreWriter</c> stores.
/// The storage names match the writer's schema: key, content, documentid, embedding, plus the
/// metadata that <c>PageNumberProcessor</c> adds (page, sha256).
/// </summary>
public sealed class BookPage
{
    [VectorStoreKey(StorageName = "key")]
    public Guid Key { get; set; }

    [VectorStoreData(StorageName = "content")]
    public string Content { get; set; } = "";

    [VectorStoreData(StorageName = "documentid", IsIndexed = true)]
    public string DocumentId { get; set; } = "";

    [VectorStoreData(StorageName = "page")]
    public int Page { get; set; }

    [VectorStoreData(StorageName = "sha256")]
    public string Sha256 { get; set; } = "";

    [VectorStoreVector(SemanticSearch.Dimensions, StorageName = "embedding")]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>Reads the <c>book-pages</c> collection. The ingestion pipeline creates it on first write.</summary>
public sealed class BookPages(VectorStore store)
{
    public const string CollectionName = "book-pages";

    private readonly VectorStoreCollection<Guid, BookPage> _collection = store.GetCollection<Guid, BookPage>(CollectionName);

    public async Task<string?> GetHashAsync(Guid bookId, CancellationToken cancellationToken)
    {
        if (!await _collection.CollectionExistsAsync(cancellationToken))
        {
            return null;
        }

        string id = bookId.ToString();
        await foreach (BookPage page in _collection.GetAsync(p => p.DocumentId == id, 1, cancellationToken: cancellationToken))
        {
            return page.Sha256;
        }

        return null;
    }

    public async Task<int> CountAsync(Guid bookId, CancellationToken cancellationToken)
    {
        if (!await _collection.CollectionExistsAsync(cancellationToken))
        {
            return 0;
        }

        string id = bookId.ToString();
        return await _collection.GetAsync(p => p.DocumentId == id, 10_000, cancellationToken: cancellationToken).CountAsync(cancellationToken);
    }

    public async Task<List<SearchHit>> SearchAsync(ReadOnlyMemory<float> query, int top, CancellationToken cancellationToken)
    {
        if (!await _collection.CollectionExistsAsync(cancellationToken))
        {
            return [];
        }

        return await _collection.SearchAsync(query, top, cancellationToken: cancellationToken)
            .Select(r => new SearchHit(Guid.Parse(r.Record.DocumentId), r.Score ?? 0, r.Record.Page, r.Record.Content))
            .ToListAsync(cancellationToken);
    }
}
