# Architecture

Jellyfin stays as it is. A plugin inside Jellyfin forwards search and
"More Like This" to a separate .NET service, and the service does the AI work with
the .NET AI building blocks. Aspire starts everything.

```mermaid
flowchart LR
    user([Jellyfin user]) --> plugin

    subgraph jellyfin [Jellyfin 12 container]
        plugin[Semantic Discovery plugin<br/>search provider, similar items, sync task]
    end

    subgraph service [SemanticService: .NET 11 minimal API]
        reading[Reading<br/>DocumentExtractionClient + middleware]
        ingestion[Ingestion<br/>IngestionPipeline]
        search[Search<br/>VectorStore collections]
    end

    plugin -- "POST /index/media<br/>PUT /index/books/{id}" --> ingestion
    plugin -- "GET /search<br/>GET /similar/{id}" --> search

    ingestion --> reading
    ingestion --> qdrant[(Qdrant)]
    search --> qdrant
    ingestion & search -- IEmbeddingGenerator --> ollama[Ollama<br/>embeddinggemma]
    reading -- "IChatClient<br/>OrderedFailoverChatClient" --> azure[Azure OpenAI<br/>vision model]
    reading -. fallback .-> vlm[Ollama<br/>qwen3-vl]
```

Aspire ([AppHost.cs](../src/Jellyfin.SemanticDiscovery.AppHost/AppHost.cs)) starts
Qdrant, Ollama, the service, and Jellyfin, then runs `jellyfin-setup` once to create
the libraries, scan them, and run the first sync.

## Indexing

The plugin's **Sync semantic index** task runs at startup and then every 24 hours.

```mermaid
sequenceDiagram
    participant Sync as SyncTask (plugin)
    participant API as SemanticService
    participant Pipe as IngestionPipeline
    participant Q as Qdrant

    Sync->>API: POST /index/media (id, title, overview, genres, transcript)
    API->>Q: embed and upsert into "media"
    loop each PDF book
        Sync->>API: PUT /index/books/{id} (the PDF)
        API->>Pipe: ProcessAsync(file)
        Note over Pipe: ExtractionReader → book reader<br/>(PdfPig, or a vision model for scans)
        Pipe->>Pipe: HeaderChunker (384 tokens) → PageNumberProcessor
        Pipe->>Q: VectorStoreWriter → "book-pages"
    end
```

- **`media`** has one point per Jellyfin item, keyed by the item id. Its text is
  the title, overview, genres, tags, year, and, for movies and songs, the transcript.
- **`book-pages`** has the chunks that `VectorStoreWriter` writes. `documentid` is
  the Jellyfin item id. The metadata holds the page number and the PDF's hash, so
  the next sync skips an unchanged book.

### Reading a book

The book reader is a keyed `DocumentExtractionClient` with middleware around it
(see [PageReading.cs](../src/Jellyfin.SemanticDiscovery.SemanticService/Reading/PageReading.cs)):

```mermaid
flowchart LR
    pdf[PDF] --> log[UseLogging] --> fallback[ScannedPdfFallback]
    fallback --> pdfpig[PdfPigDocumentExtractionClient<br/>Docstrum layout, headings by font size]
    fallback -- "text layer under 100 characters" --> raster[PdfPageRasterizer<br/>PDFtoImage, 150 DPI]
    raster --> retry[PlainMarkdownRetry] --> vlm[VisionLanguageModelDocumentExtractionClient]
    vlm --> chat[IChatClient<br/>OrderedFailoverChatClient]
    chat --> r1[1. page-reader<br/>Azure OpenAI]
    chat -. on failure .-> r2[2. vision-model<br/>Ollama]
```

Only the chat routes that have a connection string are registered. With no routes
(the `ci` profile) the fallback middleware isn't added, and a scanned book gets no
chunks.

## Search

```mermaid
sequenceDiagram
    participant J as Jellyfin search box
    participant P as SemanticSearchProvider
    participant API as SemanticService
    participant Q as Qdrant

    J->>P: SearchAsync(query, user)
    P->>API: GET /search?q=...
    API->>Q: embed the query, then search "media" and "book-pages"
    API-->>P: best score per item (at least 0.35), with the matching page for books
    P->>P: keep the items GetItemById(id, user) returns, of the requested types
    P-->>J: ranked results
```

**More Like This** reads the item's vector from `media` (`IncludeVectors = true`),
searches with it, and drops the item itself.

Permissions stay in Jellyfin. The service knows nothing about users; the plugin
drops every hit the current user can't load.

## Profiles

| | `ci` | `demo` (default) |
| --- | --- | --- |
| Qdrant | No volume; starts empty each run | `jellyfin-semantic-discovery-qdrant` volume |
| Embeddings | Ollama `embeddinggemma` | Same |
| Chat routes | None | `page-reader` if its connection string is set; `vision-model` if `SEMANTIC_DISCOVERY_LOCAL_VISION=true` |
| Scanned book | Not searchable by its contents | Read by the vision model |
