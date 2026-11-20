using CommunityToolkit.VectorData.Qdrant;
using Jellyfin.SemanticDiscovery.SemanticService.Ingestion;
using Jellyfin.SemanticDiscovery.SemanticService.Reading;
using Jellyfin.SemanticDiscovery.SemanticService.Search;
using Qdrant.Client;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Embeddings: a local Ollama model, exposed as IEmbeddingGenerator<string, Embedding<float>>.
builder.AddOllamaApiClient("embedding-model").AddEmbeddingGenerator();

// Vector store: the Qdrant container from the AppHost, exposed as a VectorStore.
// The store picks up the IEmbeddingGenerator above, so collections can search by text.
builder.AddQdrantClient("qdrant");
builder.Services.AddQdrantVectorStore(sp => sp.GetRequiredService<QdrantClient>());

// Reading: PdfPig for born-digital PDFs, a vision model (behind a failover IChatClient) for scans.
builder.AddPageReading();

builder.Services.AddSingleton<MediaIndex>();
builder.Services.AddSingleton<BookPages>();
builder.Services.AddSingleton<BookIngestion>();
builder.Services.AddSingleton<SemanticSearch>();

WebApplication app = builder.Build();
app.MapDefaultEndpoints();

// The Jellyfin plugin sends item metadata (title, overview, genres, transcript text).
app.MapPost("/index/media", async (MediaItem[] items, MediaIndex media, CancellationToken ct) =>
{
    await media.UpsertAsync(items, ct);
    return Results.Ok(new { indexed = items.Length });
});

// ...and the PDF behind each book. Ingestion runs inside the request, so when the
// plugin's sync task finishes, every book is searchable.
app.MapPut("/index/books/{id:guid}", async (Guid id, HttpRequest request, BookIngestion books, CancellationToken ct) =>
    Results.Ok(await books.IngestAsync(id, request.Body, ct)));

app.MapGet("/search", (string q, int? limit, SemanticSearch search, CancellationToken ct) =>
    search.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 100), ct));

app.MapGet("/similar/{id:guid}", (Guid id, int? limit, SemanticSearch search, CancellationToken ct) =>
    search.SimilarAsync(id, Math.Clamp(limit ?? 12, 1, 100), ct));

app.Run();
