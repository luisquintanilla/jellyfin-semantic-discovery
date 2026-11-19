using System.Net;
using System.Text;
using Jellyfin.SemanticDiscovery.Extraction.Infrastructure;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

public sealed class OllamaChatClientTests
{
    [Fact]
    public async Task ReturnsAssistantText()
    {
        using HttpClient httpClient = new(new JsonHandler(
            """
            {
              "model": "qwen3-vl:8b-instruct",
              "created_at": "2026-11-18T00:00:00Z",
              "message": { "content": "# Heading\nVisual text" }
            }
            """));
        using OllamaChatClient client = new(
            httpClient,
            new Uri("http://ollama/"),
            "qwen3-vl:8b-instruct");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "extract")]);

        Assert.Equal("# Heading\nVisual text", response.Text);
    }

    [Fact]
    public async Task RejectsModelOverride()
    {
        using HttpClient httpClient = new(new JsonHandler("{}"));
        using OllamaChatClient client = new(
            httpClient,
            new Uri("http://ollama/"),
            "qwen3-vl:8b-instruct");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "extract")],
                    new ChatOptions { ModelId = "qwen3-vl:4b-instruct" }));

        Assert.Contains(
            "No model fallback or override is permitted",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnexpectedServedModel()
    {
        using HttpClient httpClient = new(new JsonHandler(
            """
            {
              "model": "qwen3-vl:4b-instruct",
              "created_at": "2026-11-18T00:00:00Z",
              "message": { "content": "text" }
            }
            """));
        using OllamaChatClient client = new(
            httpClient,
            new Uri("http://ollama/"),
            "qwen3-vl:8b-instruct");

        InvalidDataException exception =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "extract")]));

        Assert.Contains(
            "instead of required model",
            exception.Message,
            StringComparison.Ordinal);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
