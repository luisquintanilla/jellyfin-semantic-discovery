using System.Diagnostics;
using System.Security.Cryptography;
using Jellyfin.SemanticDiscovery.SemanticService.Reading;
using Jellyfin.SemanticDiscovery.SemanticService.Search;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

namespace Jellyfin.SemanticDiscovery.SemanticService.Ingestion;

public sealed record BookIngestionResult(Guid Id, string Status, int Chunks, long ElapsedMs);

/// <summary>
/// Read a PDF, split it into chunks, embed them, and store them in Qdrant: one
/// <see cref="IngestionPipeline{T}"/> with a reader, a chunker, a processor, and a writer.
/// </summary>
public sealed partial class BookIngestion(
    [FromKeyedServices(PageReading.BookReader)] DocumentExtractionClient reader,
    VectorStore store,
    BookPages pages,
    ILogger<BookIngestion> logger)
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    public async Task<BookIngestionResult> IngestAsync(Guid id, Stream pdf, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();

        // The pipeline reads files, so stage the upload under the item id (it becomes the document id).
        string path = Path.Combine(Path.GetTempPath(), $"{id}.pdf");
        await using (FileStream file = File.Create(path))
        {
            await pdf.CopyToAsync(file, cancellationToken);
        }

        try
        {
            string sha256;
            await using (FileStream file = File.OpenRead(path))
            {
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            }

            if (await pages.GetHashAsync(id, cancellationToken) == sha256)
            {
                return new(id, "unchanged", 0, timer.ElapsedMilliseconds);
            }

            using VectorStoreWriter<string> writer = new(store, SemanticSearch.Dimensions, new()
            {
                CollectionName = BookPages.CollectionName,
                IncrementalIngestion = true, // replaces this book's old chunks
            });
            using IngestionPipeline<string> pipeline = new(
                new ExtractionReader(reader),
                new HeaderChunker(new(Tokenizer) { MaxTokensPerChunk = 384 }),
                writer);
            pipeline.ChunkProcessors.Add(new PageNumberProcessor(sha256));

            await foreach (IngestionResult result in pipeline.ProcessAsync([new FileInfo(path)], cancellationToken))
            {
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"Ingestion failed for {id}.", result.Exception);
                }
            }

            int chunks = await pages.CountAsync(id, cancellationToken);
            LogIngested(id, chunks, timer.ElapsedMilliseconds);
            return new(id, "ingested", chunks, timer.ElapsedMilliseconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [LoggerMessage(LogLevel.Information, "Ingested book {Id}: {Chunks} chunks in {ElapsedMs} ms.")]
    private partial void LogIngested(Guid id, int chunks, long elapsedMs);
}
