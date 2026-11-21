using Jellyfin.SemanticDiscovery.SemanticService.Ingestion;
using Jellyfin.SemanticDiscovery.SemanticService.Reading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.SemanticDiscovery.Tests;

public sealed class ReadingTests
{
    [Fact]
    public void ExtractionReader_maps_headings_tables_and_figures_and_keeps_page_numbers()
    {
        ExtractionDocumentSection section = OnPage(2, new ExtractionDocumentSection(new TextContent("")));
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Chapter 3"), "heading") { HeadingLevel = 1 });
        section.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Body text."), null));
        ExtractionDocumentTableElement table = new(new TextContent(""), 2, 2);
        table.Cells[0, 0] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("Model"), null), 1, 1, null);
        table.Cells[0, 1] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("Score"), null), 1, 1, null);
        table.Cells[1, 0] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("a|b"), null), 1, 1, null);
        table.Cells[1, 1] = new ExtractionDocumentTableCell(new ExtractionDocumentTextElement(new TextContent("0.9"), null), 1, 1, null);
        section.Elements.Add(table);
        section.Elements.Add(new ExtractionDocumentImageElement(new TextContent(""), null) { AlternativeText = "A bar chart" });
        section.Elements.Add(OnPage(3, new ExtractionDocumentTextElement(new TextContent("Overflow."), null)));

        IngestionDocument document = ExtractionReader.Map(Document(section), "book");

        IngestionDocumentElement[] elements = [.. document.Sections.Single().Elements];
        Assert.Equal(5, elements.Length);
        IngestionDocumentHeader header = Assert.IsType<IngestionDocumentHeader>(elements[0]);
        Assert.Equal(1, header.Level);
        Assert.Equal("Chapter 3", header.GetMarkdown().Trim().TrimStart('#', ' '));
        Assert.Equal("| Model | Score |\n| --- | --- |\n| a\\|b | 0.9 |", elements[2].GetMarkdown().ReplaceLineEndings("\n").Trim());
        Assert.Equal("Figure: A bar chart", elements[3].GetMarkdown().Trim());
        Assert.Equal([2, 2, 2, 2, 3], elements.Select(e => e.PageNumber ?? 0));
    }

    [Fact]
    public void ExtractionReader_skips_page_furniture_and_flattens_nested_sections()
    {
        ExtractionDocumentSection inner = new(new TextContent(""));
        inner.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Nested paragraph."), null));
        ExtractionDocumentSection outer = new(new TextContent(""));
        outer.Elements.Add(new ExtractionDocumentTextElement(new TextContent("Running header"), "pageHeader"));
        outer.Elements.Add(inner);
        outer.Elements.Add(new ExtractionDocumentTextElement(new TextContent("7"), "pageNumber"));

        IngestionDocument document = ExtractionReader.Map(Document(outer), "book");

        Assert.Equal("Nested paragraph.", Assert.Single(document.Sections.Single().Elements).GetMarkdown().Trim());
    }

    [Fact]
    public async Task ScannedPdfFallback_keeps_the_text_layer_when_there_is_enough_text()
    {
        FakeReader text = new("A real text layer with plenty of characters.");
        FakeReader vision = new("from vision");

        ExtractionDocument result = await Fallback(text, vision, minimumCharacters: 10).ExtractAsync(Pdf(), "book.pdf", "application/pdf");

        Assert.Same(text.Result, result);
        Assert.Equal(0, vision.Calls);
    }

    [Fact]
    public async Task ScannedPdfFallback_sends_the_same_bytes_to_the_vision_reader_when_the_text_layer_is_empty()
    {
        FakeReader text = new("  ");
        FakeReader vision = new("from vision");

        ExtractionDocument result = await Fallback(text, vision, minimumCharacters: 10).ExtractAsync(Pdf(), "scan.pdf", "application/pdf");

        Assert.Same(vision.Result, result);
        Assert.Equal("%PDF-test", vision.LastInput);
    }

    [Fact]
    public async Task ScannedPdfFallback_uses_the_vision_reader_when_forced()
    {
        FakeReader text = new("A real text layer with plenty of characters.");
        FakeReader vision = new("from vision");

        ExtractionDocument result = await Fallback(text, vision, minimumCharacters: 10, forceVision: true)
            .ExtractAsync(Pdf(), "book.pdf", "application/pdf");

        Assert.Same(vision.Result, result);
    }

    [Fact]
    public async Task PdfPageRasterizer_sends_each_page_of_the_scanned_book_as_a_png_and_adds_page_numbers()
    {
        PageRecorder pages = new();
        await using FileStream pdf = File.OpenRead("retrieval-field-notes.pdf");

        ExtractionDocument result = await new PdfPageRasterizer(pages).ExtractAsync(pdf, "scan.pdf", "application/pdf");

        Assert.True(pages.Inputs.Count > 1);
        Assert.All(pages.Inputs, input =>
        {
            Assert.Equal("image/png", input.MediaType);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], input.Header);
        });
        Assert.Equal(Enumerable.Range(1, pages.Inputs.Count), result.Sections.Select(s => s.Sources[0].Page.PageNumber ?? 0));
    }

    [Fact]
    public async Task OrderedFailoverChatClient_moves_to_the_next_route_when_the_first_fails()
    {
        FakeChat cloud = new(_ => throw new HttpRequestException("429 Too Many Requests"));
        FakeChat local = new(_ => "page text");
        using OrderedFailoverChatClient chat = new([cloud, local], leaveOpen: true);

        ChatResponse response = await chat.GetResponseAsync("read this page");

        Assert.Equal("page text", response.Text);
        Assert.Equal(1, cloud.Calls);
        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public void AddPageReading_without_chat_routes_registers_only_the_text_layer_reader()
    {
        using IHost host = Build([]);

        Assert.Null(host.Services.GetService<IChatClient>());
        Assert.Null(Reader(host).GetService(typeof(ScannedPdfFallback)));
    }

    [Fact]
    public void AddPageReading_with_a_chat_route_adds_the_scanned_pdf_fallback()
    {
        using IHost host = Build(new() { ["ConnectionStrings:vision-model"] = "Endpoint=http://localhost:1;Model=qwen3-vl:8b-instruct" });

        Assert.NotNull(host.Services.GetService<IChatClient>());
        Assert.NotNull(Reader(host).GetService(typeof(ScannedPdfFallback)));
    }

    private static IHost Build(Dictionary<string, string?> settings)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPageReading();
        return builder.Build();
    }

    // The registered reader is wrapped in logging middleware; GetService looks through the wrappers.
    private static DocumentExtractionClient Reader(IHost host) =>
        host.Services.GetRequiredKeyedService<DocumentExtractionClient>(PageReading.BookReader);

    private static ScannedPdfFallback Fallback(FakeReader text, FakeReader vision, int minimumCharacters, bool forceVision = false) =>
        new(text, vision, minimumCharacters, forceVision, NullLogger<ScannedPdfFallback>.Instance);

    private static MemoryStream Pdf() => new("%PDF-test"u8.ToArray());

    private static ExtractionDocument Document(ExtractionDocumentSection section)
    {
        ExtractionDocument document = new("book", new TextContent(""));
        document.Sections.Add(section);
        return document;
    }

    private static T OnPage<T>(int page, T element)
        where T : ExtractionDocumentElement
    {
        element.Sources.Add(new ExtractionDocumentSourceReference(new ExtractionDocumentSourcePage($"page-{page}", page, null, null, null), [], null));
        return element;
    }

    private sealed class FakeReader : DocumentExtractionClient
    {
        public FakeReader(string text)
        {
            Result = new ExtractionDocument("doc", new TextContent(text));
            ExtractionDocumentSection section = new(new TextContent(""));
            section.Elements.Add(new ExtractionDocumentTextElement(new TextContent(text), null));
            Result.Sections.Add(section);
        }

        public ExtractionDocument Result { get; }

        public int Calls { get; private set; }

        public string? LastInput { get; private set; }

        public override async Task<ExtractionDocument> ExtractAsync(
            Stream stream, string identifier, string mediaType,
            DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            using StreamReader reader = new(stream, leaveOpen: true);
            LastInput = await reader.ReadToEndAsync(cancellationToken);
            return Result;
        }
    }

    private sealed class PageRecorder : DocumentExtractionClient
    {
        public List<(string MediaType, byte[] Header)> Inputs { get; } = [];

        public override async Task<ExtractionDocument> ExtractAsync(
            Stream stream, string identifier, string mediaType,
            DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, cancellationToken);
            Inputs.Add((mediaType, header));

            ExtractionDocument page = new(identifier, new TextContent("page text"));
            page.Sections.Add(new ExtractionDocumentSection(new TextContent("page text")));
            return page;
        }
    }

    private sealed class FakeChat(Func<IEnumerable<ChatMessage>, string> respond) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, respond(messages))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
