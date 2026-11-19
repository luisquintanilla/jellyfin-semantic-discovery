using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public sealed class OllamaEmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>,
    IDisposable
{
    private readonly OllamaEmbeddingOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    private OllamaEmbeddingGenerator(
        OllamaEmbeddingOptions options,
        HttpClient httpClient,
        bool ownsHttpClient,
        string modelDigest,
        ApplicationChunkTokenizer tokenizer,
        string extractionProfile)
    {
        _options = options;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        Identity = OllamaIndexIdentity.Create(
            options,
            modelDigest,
            tokenizer,
            extractionProfile);
    }

    public IndexIdentity Identity { get; }

    public static async ValueTask<OllamaEmbeddingGenerator> CreateAsync(
        OllamaEmbeddingOptions options,
        ApplicationChunkTokenizer tokenizer,
        string extractionProfile,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenizer);
        options.Validate();

        bool ownsHttpClient = httpClient is null;
        httpClient ??= new HttpClient
        {
            BaseAddress = EnsureTrailingSlash(options.Endpoint),
            Timeout = options.Timeout,
        };
        httpClient.BaseAddress ??= EnsureTrailingSlash(options.Endpoint);

        try
        {
            OllamaTagsResponse tags =
                await httpClient.GetFromJsonAsync<OllamaTagsResponse>(
                    "api/tags",
                    cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Ollama returned an empty model list.");
            OllamaModel? model = tags.Models.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.Name,
                    options.Model,
                    StringComparison.Ordinal) ||
                string.Equals(
                    candidate.Model,
                    options.Model,
                    StringComparison.Ordinal));
            if (model is null)
            {
                throw new InvalidOperationException(
                    $"Required Ollama model '{options.Model}' is not installed. " +
                    "No fallback model is permitted.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(model.Digest);
            return new OllamaEmbeddingGenerator(
                options,
                httpClient,
                ownsHttpClient,
                model.Digest,
                tokenizer,
                extractionProfile);
        }
        catch
        {
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }

            throw;
        }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (options?.Dimensions is { } dimensions &&
            dimensions != _options.Dimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"The configured model emits {_options.Dimensions} dimensions.");
        }

        string[] inputs = values
            .Take(_options.MaximumInputsPerRequest + 1)
            .ToArray();
        if (inputs.Length > _options.MaximumInputsPerRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"A request may contain at most {_options.MaximumInputsPerRequest} inputs.");
        }

        if (inputs.Any(static value => value is null))
        {
            throw new ArgumentException(
                "Embedding inputs cannot contain null values.",
                nameof(values));
        }

        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            "api/embed",
            new OllamaEmbedRequest(_options.Model, inputs),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        OllamaEmbedResponse payload =
            await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(
                cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "Ollama returned an empty embedding response.");
        if (!string.Equals(
                payload.Model,
                _options.Model,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Ollama served model '{payload.Model}' instead of " +
                $"required model '{_options.Model}'.");
        }

        if (payload.Embeddings.Length != inputs.Length)
        {
            throw new InvalidDataException(
                "Ollama embedding count did not match the input count.");
        }

        GeneratedEmbeddings<Embedding<float>> generated =
            new(payload.Embeddings.Length);
        foreach (float[] vector in payload.Embeddings)
        {
            ValidateVector(vector);
            VectorMath.Normalize(vector);
            generated.Add(new Embedding<float>(vector)
            {
                ModelId = _options.Model,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        return generated;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private void ValidateVector(float[] vector)
    {
        if (vector.Length != _options.Dimensions)
        {
            throw new InvalidDataException(
                $"Ollama returned {vector.Length} dimensions; " +
                $"{_options.Dimensions} were required.");
        }

        if (vector.Any(static value => !float.IsFinite(value)))
        {
            throw new InvalidDataException(
                "Ollama returned a non-finite embedding value.");
        }
    }

    private static Uri EnsureTrailingSlash(Uri endpoint) =>
        endpoint.AbsoluteUri.EndsWith('/')
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + '/', UriKind.Absolute);

    private sealed record OllamaEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record OllamaEmbedResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("embeddings")] float[][] Embeddings);

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] OllamaModel[] Models);

    private sealed record OllamaModel(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("digest")] string Digest);
}
