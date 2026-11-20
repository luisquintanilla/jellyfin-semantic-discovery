# Reading

Turns a PDF into a structured document (headings, paragraphs, tables, figures, page
numbers) with `Microsoft.Extensions.DocumentExtraction`.

**Key file:** [PageReading.cs](PageReading.cs). It registers one keyed
`DocumentExtractionClient` (`"books"`) and builds it from middleware, the same way
`IChatClient` pipelines are built:

```csharp
builder.Services.AddKeyedDocumentExtractionClient(BookReader, _ => new PdfPigDocumentExtractionClient(...))
    .UseLogging()
    .Use((inner, sp) => new ScannedPdfFallback(inner, CreateVisionReader(chat), ...));
```

| File | Role |
| --- | --- |
| [PageReading.cs](PageReading.cs) | Registrations: chat routes, the failover `IChatClient`, and the reader pipeline |
| [ScannedPdfFallback.cs](ScannedPdfFallback.cs) | Middleware. If PdfPig finds almost no text, the PDF goes to the vision reader. |
| [PdfPageRasterizer.cs](PdfPageRasterizer.cs) | Middleware. Renders each page to PNG with [PDFtoImage](https://github.com/sungaila/PDFtoImage) and adds page numbers. Also holds `PlainMarkdownRetry`, which re-reads a page as plain markdown if a small model's structured output doesn't parse. |

## The chat model behind the vision reader

`OrderedFailoverChatClient` (Microsoft.Extensions.AI, experimental `MEAI001`) wraps
the configured routes and tries them in order:

1. `page-reader`: an Azure OpenAI deployment, authenticated with Microsoft Entra ID.
2. `vision-model`: a local Ollama model. Its options (temperature 0, `think = false`,
   a larger context) are set with `ConfigureOptions`.

A route is registered only if its connection string exists. With no routes, there's
no vision reader, and PdfPig handles everything.

## Swapping things

- **A different text reader**: replace `PdfPigDocumentExtractionClient` with any
  `DocumentExtractionClient` (for example, Azure Document Intelligence).
- **A different vision model**: add a connection string. Any `IChatClient` that accepts
  images works.

For more patterns (routing by file type, OCR, layout models), see
[dotnet-document-extraction-samples](https://github.com/luisquintanilla/dotnet-document-extraction-samples).
