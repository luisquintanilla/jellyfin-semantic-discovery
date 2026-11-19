using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.Extraction.Infrastructure;

public sealed class OllamaChatClient(
    HttpClient httpClient,
    Uri endpoint,
    string model) : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _chatEndpoint = new(endpoint, "/api/chat");
    private readonly string _model = string.IsNullOrWhiteSpace(model)
        ? throw new ArgumentException(
            "An explicit Ollama model is required; no model fallback is used.",
            nameof(model))
        : model;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string requestedModel = options?.ModelId ?? _model;
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            throw new InvalidOperationException(
                "An explicit Ollama model is required; no model fallback is used.");
        }
        if (!string.Equals(requestedModel, _model, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Requested model '{requestedModel}' does not match the " +
                $"configured model '{_model}'. No model fallback or override " +
                "is permitted.");
        }

        List<OllamaMessage> requestMessages = [];
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            requestMessages.Add(
                new OllamaMessage(
                    ChatRole.System.Value,
                    options.Instructions,
                    null));
        }

        requestMessages.AddRange(messages.Select(ConvertMessage));
        object? format = options?.ResponseFormat switch
        {
            ChatResponseFormatJson json when json.Schema is JsonElement schema =>
                schema,
            ChatResponseFormatJson => "json",
            _ => null,
        };
        OllamaChatRequest request = new(
            requestedModel,
            requestMessages,
            Stream: false,
            Think: false,
            format,
            CreateRequestOptions(options));
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            _chatEndpoint,
            request,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Ollama returned {(int)response.StatusCode}: {responseBody}",
                null,
                response.StatusCode);
        }

        OllamaChatResponse result =
            JsonSerializer.Deserialize<OllamaChatResponse>(
                responseBody,
                JsonOptions)
            ?? throw new InvalidDataException("Ollama returned an empty response.");
        if (!string.Equals(
                result.Model,
                _model,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Ollama served model '{result.Model}' instead of required " +
                $"model '{_model}'.");
        }
        return new ChatResponse(
            new ChatMessage(ChatRole.Assistant, result.Message.Content))
        {
            ModelId = result.Model,
            CreatedAt = result.CreatedAt,
            RawRepresentation = result,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate>
        GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(
            messages,
            options,
            cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            response.Text)
        {
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            RawRepresentation = response.RawRepresentation,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceKey is null && serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(
                nameof(OllamaChatClient),
                _chatEndpoint,
                _model);
        }

        return null;
    }

    public void Dispose()
    {
    }

    private static OllamaMessage ConvertMessage(ChatMessage message)
    {
        List<string>? images = null;
        foreach (AIContent content in message.Contents)
        {
            if (content is TextContent)
            {
                continue;
            }

            if (content is not DataContent data ||
                data.MediaType is null ||
                !data.MediaType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "The extraction Ollama client accepts page images only; raw PDFs and other binary inputs are rejected.");
            }

            images ??= [];
            images.Add(Convert.ToBase64String(data.Data.Span));
        }

        return new OllamaMessage(
            message.Role.Value,
            message.Text,
            images);
    }

    private static OllamaRequestOptions CreateRequestOptions(
        ChatOptions? options) =>
        new(
            options?.Temperature ?? 0F,
            options?.TopP,
            options?.TopK,
            options?.Seed ?? 42,
            options?.MaxOutputTokens ?? 2048);

    private sealed record OllamaChatRequest(
        string Model,
        IReadOnlyList<OllamaMessage> Messages,
        bool Stream,
        bool Think,
        object? Format,
        OllamaRequestOptions Options);

    private sealed record OllamaMessage(
        string Role,
        string Content,
        IReadOnlyList<string>? Images);

    private sealed record OllamaRequestOptions(
        float? Temperature,
        float? TopP,
        int? TopK,
        long? Seed,
        int? NumPredict);

    private sealed record OllamaChatResponse(
        string? Model,
        DateTimeOffset? CreatedAt,
        OllamaResponseMessage Message);

    private sealed record OllamaResponseMessage(string Content);
}
