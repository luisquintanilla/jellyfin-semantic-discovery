using Jellyfin.SemanticDiscovery.Core.Identity;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

/// <summary>
/// Enforces the index contract (batch size, dimensions, finite values) on
/// top of any <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>; the
/// plugin wraps OllamaSharp's <see cref="OllamaApiClient"/>.
/// </summary>
public sealed class ValidatingEmbeddingGenerator :
    DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly int _dimensions;
    private readonly int _maximumInputsPerRequest;
    private readonly HttpClient? _ownedHttpClient;

    private ValidatingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
        int dimensions,
        int maximumInputsPerRequest,
        IndexIdentity identity,
        HttpClient? ownedHttpClient)
        : base(innerGenerator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumInputsPerRequest);
        ArgumentNullException.ThrowIfNull(identity);
        _dimensions = dimensions;
        _maximumInputsPerRequest = maximumInputsPerRequest;
        _ownedHttpClient = ownedHttpClient;
        Identity = identity;
    }

    public IndexIdentity Identity { get; }

    public static async ValueTask<ValidatingEmbeddingGenerator>
        CreateOllamaAsync(
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
        httpClient ??= new HttpClient { Timeout = options.Timeout };
        httpClient.BaseAddress ??= options.Endpoint;
        OllamaApiClient ollama = new(httpClient, options.Model);
        try
        {
            OllamaSharp.Models.Model? model =
                (await ollama.ListLocalModelsAsync(cancellationToken)
                    .ConfigureAwait(false))
                .SingleOrDefault(candidate =>
                    string.Equals(
                        candidate.Name,
                        options.Model,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        candidate.ModelName,
                        options.Model,
                        StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Required Ollama model '{options.Model}' is not " +
                    "installed. No fallback model is permitted.");
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Digest);
            return new ValidatingEmbeddingGenerator(
                ollama,
                options.Dimensions,
                options.MaximumInputsPerRequest,
                OllamaIndexIdentity.Create(
                    options,
                    model.Digest,
                    tokenizer,
                    extractionProfile),
                ownsHttpClient ? httpClient : null);
        }
        catch
        {
            ollama.Dispose();
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }

            throw;
        }
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>>
        GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (options?.Dimensions is { } dimensions &&
            dimensions != _dimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"The configured model emits {_dimensions} dimensions.");
        }

        string[] inputs = values
            .Take(_maximumInputsPerRequest + 1)
            .ToArray();
        if (inputs.Length > _maximumInputsPerRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"A request may contain at most {_maximumInputsPerRequest} inputs.");
        }

        if (inputs.Any(static value => value is null))
        {
            throw new ArgumentException(
                "Embedding inputs cannot contain null values.",
                nameof(values));
        }

        GeneratedEmbeddings<Embedding<float>> generated =
            await base.GenerateAsync(inputs, options, cancellationToken)
                .ConfigureAwait(false);
        if (generated.Count != inputs.Length)
        {
            throw new InvalidDataException(
                "Embedding count did not match the input count.");
        }

        foreach (Embedding<float> embedding in generated)
        {
            ReadOnlySpan<float> vector = embedding.Vector.Span;
            if (vector.Length != _dimensions)
            {
                throw new InvalidDataException(
                    $"The embedding model returned {vector.Length} " +
                    $"dimensions; {_dimensions} were required.");
            }

            foreach (float value in vector)
            {
                if (!float.IsFinite(value))
                {
                    throw new InvalidDataException(
                        "The embedding model returned a non-finite value.");
                }
            }
        }

        return generated;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _ownedHttpClient?.Dispose();
        }
    }
}
