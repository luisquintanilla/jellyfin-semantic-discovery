using System.Net;
using System.Text;
using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Embedding;

public sealed class OllamaEmbeddingGeneratorTests
{
    [Fact]
    public async Task CreateAndGenerateRequireExactModelAndNormalizeVector()
    {
        using HttpClient client = new(new SequenceHandler(
            """
            {"models":[{"name":"embeddinggemma:latest","model":"embeddinggemma:latest","digest":"ABC123"}]}
            """,
            """
            {"model":"embeddinggemma:latest","embeddings":[[3,4,0]]}
            """))
        {
            BaseAddress = new Uri("http://ollama/"),
        };
        ApplicationChunkTokenizer tokenizer = new();
        using OllamaEmbeddingGenerator generator =
            await OllamaEmbeddingGenerator.CreateAsync(
                new OllamaEmbeddingOptions
                {
                    Endpoint = client.BaseAddress,
                    Dimensions = 3,
                },
                tokenizer,
                "qwen3-vl:8b",
                client);

        float[] vector = (await generator.GenerateAsync(["text"]))
            .Single()
            .Vector
            .ToArray();

        Assert.Equal([0.6F, 0.8F, 0F], vector);
        Assert.Equal("abc123", generator.Identity.ModelHash);
        Assert.Contains(
            "qwen3-vl:8b",
            generator.Identity.IndexingPipelineVersion,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRejectsMissingModelWithoutFallback()
    {
        using HttpClient client = new(new SequenceHandler(
            """
            {"models":[{"name":"other","model":"other","digest":"ABC123"}]}
            """))
        {
            BaseAddress = new Uri("http://ollama/"),
        };

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await OllamaEmbeddingGenerator.CreateAsync(
                    new OllamaEmbeddingOptions
                    {
                        Endpoint = client.BaseAddress,
                    },
                    new ApplicationChunkTokenizer(),
                    "qwen3-vl:8b",
                    client));

        Assert.Contains(
            "No fallback model is permitted",
            exception.Message,
            StringComparison.Ordinal);
    }

    private sealed class SequenceHandler(params string[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
