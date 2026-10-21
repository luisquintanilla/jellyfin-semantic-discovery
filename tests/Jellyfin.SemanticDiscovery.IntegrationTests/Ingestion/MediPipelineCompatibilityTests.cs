using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Ingestion;

public sealed class MediPipelineCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"medi-pipeline-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task PipelineIsFileOrientedAndPartiallyWritesAroundFailure()
    {
        Type[] publicInputTypes = typeof(IngestionPipeline<string>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static method => method.Name == "ProcessAsync")
            .Select(static method => method.GetParameters()[0].ParameterType)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                typeof(IEnumerable<FileInfo>),
                typeof(DirectoryInfo),
            ],
            publicInputTypes);

        Directory.CreateDirectory(_root);
        FileInfo first = await WriteAsync("01-first.txt", "first");
        FileInfo failing = await WriteAsync("02-failing.txt", "FAIL");
        FileInfo second = await WriteAsync("03-second.txt", "second");
        CollectingWriter writer = new();
        using IngestionPipeline<string> pipeline = new(
            new FixtureReader(),
            new ParagraphChunker(),
            writer,
            new IngestionPipelineOptions(),
            NullLoggerFactory.Instance);

        List<IngestionResult> results = [];
        await foreach (IngestionResult result in pipeline.ProcessAsync(
                           [first, failing, second],
                           CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Equal([true, false, true], results.Select(
            static result => result.Succeeded));
        Assert.IsType<InvalidDataException>(results[1].Exception);
        Assert.Equal(2, writer.Batches.Count);
        Assert.Equal(["first"], writer.Batches[0]);
        Assert.Equal(["second"], writer.Batches[1]);
    }

    private async Task<FileInfo> WriteAsync(string name, string content)
    {
        string path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, content);
        return new FileInfo(path);
    }

    private sealed class FixtureReader : IngestionDocumentReader
    {
        public override async Task<IngestionDocument> ReadAsync(
            Stream source,
            string identifier,
            string mediaType,
            CancellationToken cancellationToken)
        {
            using StreamReader reader = new(source, leaveOpen: true);
            string content =
                await reader.ReadToEndAsync(cancellationToken);
            if (content == "FAIL")
            {
                throw new InvalidDataException("Fixture failure.");
            }

            IngestionDocument document = new(identifier);
            IngestionDocumentSection section = new();
            section.Elements.Add(new IngestionDocumentParagraph(content)
            {
                Text = content,
            });
            document.Sections.Add(section);
            return document;
        }
    }

    private sealed class ParagraphChunker : IngestionChunker<string>
    {
        public override async IAsyncEnumerable<IngestionChunk<string>>
            ProcessAsync(
                IngestionDocument document,
                [EnumeratorCancellation]
                CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (IngestionDocumentElement element in
                     document.EnumerateContent())
            {
                cancellationToken.ThrowIfCancellationRequested();
                IngestionDocumentParagraph paragraph =
                    Assert.IsType<IngestionDocumentParagraph>(element);
                yield return new IngestionChunk<string>(
                    paragraph.Text!,
                    document);
            }
        }
    }

    private sealed class CollectingWriter : IngestionChunkWriter<string>
    {
        public List<string[]> Batches { get; } = [];

        public override async Task WriteAsync(
            IAsyncEnumerable<IngestionChunk<string>> chunks,
            CancellationToken cancellationToken)
        {
            List<string> batch = [];
            await foreach (IngestionChunk<string> chunk in chunks
                               .WithCancellation(cancellationToken))
            {
                batch.Add(chunk.Content);
            }

            Batches.Add(batch.ToArray());
        }
    }
}
