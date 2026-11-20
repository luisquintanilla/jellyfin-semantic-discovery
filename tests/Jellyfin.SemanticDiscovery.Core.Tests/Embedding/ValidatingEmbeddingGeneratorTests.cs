using System.Net;
using System.Text;
using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Embedding;

public sealed class ValidatingEmbeddingGeneratorTests
{
    private const string Tags =
        """
        {"models":[{"name":"embeddinggemma:latest","model":"embeddinggemma:latest","digest":"ABC123"}]}
        """;

    [Fact]
    public async Task CreateAndGenerateUseOllamaSharpWithoutRenormalizing()
    {
        SequenceHandler handler = new(
            Tags,
            """
            {"model":"embeddinggemma:latest","embeddings":[[3,4,0]]}
            """);
        using ValidatingEmbeddingGenerator generator =
            await CreateAsync(handler, dimensions: 3);

        float[] vector = (await generator.GenerateAsync(["text"]))
            .Single()
            .Vector
            .ToArray();

        Assert.Equal([3F, 4F, 0F], vector);
        Assert.Equal(
            ["GET api/tags", "POST api/embed"],
            handler.Requests);
        Assert.Equal("abc123", generator.Identity.ModelHash);
        Assert.Contains(
            "qwen3-vl:8b-instruct",
            generator.Identity.IndexingPipelineVersion,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"model":"embeddinggemma:latest","embeddings":[[1,2]]}""")]
    [InlineData("""{"model":"embeddinggemma:latest","embeddings":[]}""")]
    public async Task GenerateRejectsResponsesThatBreakTheIndexContract(
        string embedResponse)
    {
        using ValidatingEmbeddingGenerator generator = await CreateAsync(
            new SequenceHandler(Tags, embedResponse),
            dimensions: 3);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await generator.GenerateAsync(["text"]));
    }

    [Fact]
    public async Task CreateRejectsMissingModelWithoutFallback()
    {
        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await CreateAsync(
                    new SequenceHandler(
                        """
                        {"models":[{"name":"other","model":"other","digest":"ABC123"}]}
                        """),
                    OllamaEmbeddingOptions.DefaultDimensions));

        Assert.Contains(
            "No fallback model is permitted",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static async Task<ValidatingEmbeddingGenerator> CreateAsync(
        SequenceHandler handler,
        int dimensions)
    {
        HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://ollama/"),
        };
        return await ValidatingEmbeddingGenerator.CreateOllamaAsync(
            new OllamaEmbeddingOptions
            {
                Endpoint = client.BaseAddress,
                Dimensions = dimensions,
            },
            new ApplicationChunkTokenizer(),
            "qwen3-vl:8b-instruct",
            client);
    }

    private sealed class SequenceHandler(params string[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(
                $"{request.Method} {request.RequestUri!.AbsolutePath.TrimStart('/')}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responses.Dequeue(),
                    Encoding.UTF8,
                    "application/json"),
                RequestMessage = request,
            });
        }
    }
}
