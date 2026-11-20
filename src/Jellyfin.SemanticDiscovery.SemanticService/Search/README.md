# Search

Two Qdrant collections, accessed through `Microsoft.Extensions.VectorData` and the
Community Toolkit Qdrant connector.

**Key file:** [SemanticSearch.cs](SemanticSearch.cs).

| File | Role |
| --- | --- |
| [MediaIndex.cs](MediaIndex.cs) | `media`: one typed `MediaRecord` per Jellyfin item. Embeds the item text and upserts it. |
| [BookPages.cs](BookPages.cs) | `book-pages`: a typed view of the chunks that `VectorStoreWriter` writes (`key`, `content`, `documentid`, `embedding`, plus `page` and `sha256`). |
| [SemanticSearch.cs](SemanticSearch.cs) | Search: embed the query, search both collections, keep the best score per item. More Like This: search with the item's own vector. |

```csharp
ReadOnlyMemory<float> vector = await embeddings.GenerateVectorAsync(query);
var hits = await media.Collection.SearchAsync(vector, limit).ToListAsync();   // items
hits.AddRange(await pages.SearchAsync(vector, limit * 3));                    // book pages
return Merge(hits, minScore, limit);                                          // best per item
```

The service doesn't filter by user. The plugin does that inside Jellyfin, which already
knows what each user can see.

## Swapping things

- **Another store**: everything here uses the `VectorStore` abstraction, so changing the
  registration in `Program.cs` is enough.
- **Hybrid or filtered search**: pass `VectorSearchOptions` (for example a `Filter`) to
  `SearchAsync`.
