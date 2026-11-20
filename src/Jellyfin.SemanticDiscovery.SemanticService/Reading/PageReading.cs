using CommunityToolkit.DocumentExtraction.PdfPig;
using CommunityToolkit.DocumentExtraction.VisionLanguageModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

namespace Jellyfin.SemanticDiscovery.SemanticService.Reading;

public static class PageReading
{
    /// <summary>The keyed <see cref="DocumentExtractionClient"/> that reads book PDFs.</summary>
    public const string BookReader = "books";

    /// <summary>Connection string names. Each one that is configured becomes a chat route.</summary>
    public static readonly string[] ChatRoutes = ["page-reader", "vision-model"];

    /// <summary>
    /// Registers the book reader: PdfPig reads the text layer, and when a PDF has
    /// almost no text (a scan), every page goes to a vision model instead.
    /// </summary>
    public static IHostApplicationBuilder AddPageReading(this IHostApplicationBuilder builder)
    {
        // Route 1: a cloud model (Azure OpenAI, Entra ID auth, no keys). Fast.
        if (builder.Configuration.GetConnectionString("page-reader") is { Length: > 0 })
        {
            builder.AddAzureOpenAIClient("page-reader").AddKeyedChatClient("page-reader");
        }

        // Route 2: a local vision model in Ollama. Private and free, but slower.
        if (builder.Configuration.GetConnectionString("vision-model") is { Length: > 0 })
        {
            builder.AddOllamaApiClient("vision-model").AddKeyedChatClient("vision-model")
                .ConfigureOptions(options =>
                {
                    // Qwen models "think" by default; turning that off keeps a page to seconds.
                    options.MaxOutputTokens ??= 8192;
                    options.Temperature ??= 0;
                    options.AdditionalProperties ??= [];
                    options.AdditionalProperties["think"] = false;
                    options.AdditionalProperties["num_ctx"] = 16384;
                });
        }

        string[] routes = ChatRoutes.Where(name => builder.Configuration.GetConnectionString(name) is { Length: > 0 }).ToArray();
        if (routes.Length > 0)
        {
            // One IChatClient for the app. It tries each route in order and moves on when one fails.
            builder.Services.AddChatClient(sp => new OrderedFailoverChatClient(
                    routes.Select(route => sp.GetRequiredKeyedService<IChatClient>(route)).ToList(),
                    leaveOpen: true))
                .UseLogging();
        }

        int minimumCharacters = builder.Configuration.GetValue("Reading:MinimumTextCharacters", 100);
        bool forceVision = builder.Configuration.GetValue("Reading:ForceVision", false);

        // The first middleware you add is the outermost: logging, then the scanned-PDF fallback, then PdfPig.
        builder.Services.AddKeyedDocumentExtractionClient(BookReader, _ => new PdfPigDocumentExtractionClient(
                pageSegmenter: DocstrumBoundingBoxes.Instance,
                roleResolver: block => block.TextLines[0].Words[0].Letters[0].PointSize >= 13 ? "heading" : null))
            .UseLogging()
            .Use((inner, sp) => sp.GetService<IChatClient>() is { } chat
                ? new ScannedPdfFallback(
                    inner,
                    CreateVisionReader(chat),
                    minimumCharacters,
                    forceVision,
                    sp.GetRequiredService<ILogger<ScannedPdfFallback>>())
                : inner);

        return builder;
    }

    /// <summary>
    /// A vision language model reads page images, so rasterize each PDF page first.
    /// Structured output returns headings and tables; if a reply doesn't match the
    /// schema, read the page again as plain markdown.
    /// </summary>
    internal static DocumentExtractionClient CreateVisionReader(IChatClient chat) =>
        new PdfPageRasterizer(new PlainMarkdownRetry(
            new VisionLanguageModelDocumentExtractionClient(chat, new() { UseStructuredOutput = true }),
            new VisionLanguageModelDocumentExtractionClient(chat, new() { UseStructuredOutput = false })));
}
