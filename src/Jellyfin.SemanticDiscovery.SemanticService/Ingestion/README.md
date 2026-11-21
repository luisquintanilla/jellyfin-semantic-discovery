# Ingestion

Turns a book PDF into searchable chunks with `Microsoft.Extensions.DataIngestion`.

**Key file:** [BookIngestion.cs](BookIngestion.cs). One `IngestionPipeline`:

```csharp
using IngestionPipeline<string> pipeline = new(
    new ExtractionReader(reader),                                       // PDF → IngestionDocument
    new HeaderChunker(new(Tokenizer) { MaxTokensPerChunk = 384 }),      // split on headings
    new VectorStoreWriter<string>(store, dimensions, new() { CollectionName = "book-pages" }));
pipeline.ChunkProcessors.Add(new PageNumberProcessor(sha256));          // tag page and hash
```

| File | Role |
| --- | --- |
| [BookIngestion.cs](BookIngestion.cs) | Builds and runs the pipeline. Skips a book whose hash hasn't changed. |
| [ExtractionReader.cs](ExtractionReader.cs) | The bridge between DocumentExtraction and DataIngestion: headings become headers (so the chunker can split on them), tables become markdown, figures keep their description, page headers and footers are dropped, and page numbers are kept. |
| [PageNumberProcessor.cs](PageNumberProcessor.cs) | Adds `page` and `sha256` to each chunk's metadata. |

`VectorStoreWriter` embeds each chunk with the registered `IEmbeddingGenerator`, and
with `IncrementalIngestion = true` it replaces a book's old chunks when the book changes.
The document id is the Jellyfin item id, so search results map straight back to
Jellyfin.

## Swapping things

- **Chunking**: replace `HeaderChunker` with another `IngestionChunker<string>`
  (for example, a semantic or section chunker).
- **Enrichment**: add more `ChunkProcessors` (summaries, keywords) before the writer.
