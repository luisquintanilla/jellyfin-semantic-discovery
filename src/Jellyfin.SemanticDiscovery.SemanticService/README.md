# SemanticService

A .NET 11 minimal API that does all the AI work. The Jellyfin plugin calls it over HTTP.

| Endpoint | What it does |
| --- | --- |
| `POST /index/media` | Embeds item text and upserts it into the `media` collection |
| `PUT /index/books/{id}` | Reads a PDF, chunks it, embeds the chunks, and writes them to `book-pages` |
| `GET /search?q=` | Searches both collections and keeps the best hit per item |
| `GET /similar/{id}` | "More Like This" from the item's own vector |
| `GET /health` | Aspire health check |

[Program.cs](Program.cs) is the place to start. It's about 50 lines and registers
every building block:

```csharp
builder.AddOllamaApiClient("embedding-model").AddEmbeddingGenerator();   // IEmbeddingGenerator
builder.AddQdrantClient("qdrant");                                       // QdrantClient
builder.Services.AddQdrantVectorStore(sp => sp.GetRequiredService<QdrantClient>()); // VectorStore
builder.AddPageReading();                                                // DocumentExtractionClient + IChatClient
```

The names (`embedding-model`, `qdrant`, `page-reader`, `vision-model`) are Aspire
connection strings that the AppHost provides.

| Folder | Building block |
| --- | --- |
| [Reading](Reading) | `DocumentExtractionClient` middleware and the failover `IChatClient` |
| [Ingestion](Ingestion) | `IngestionPipeline`: reader → chunker → processor → writer |
| [Search](Search) | `VectorStore` collections and the search/merge logic |

## Settings

| Key | Default | |
| --- | --- | --- |
| `Search:MinScore` | `0.35` | Hits below this cosine similarity are dropped |
| `Reading:MinimumTextCharacters` | `100` | A PDF with less text than this is treated as a scan |
| `Reading:ForceVision` | `false` | Send every PDF to the vision model (when a chat route exists) |

## Swapping things

- **Another vector store**: replace `AddQdrantClient` and `AddQdrantVectorStore` with
  another `VectorStore` (for example, an in-memory one or Azure AI Search). Nothing
  else references Qdrant.
- **Another embedding model**: register a different `IEmbeddingGenerator<string, Embedding<float>>`
  and update `SemanticSearch.Dimensions`.
