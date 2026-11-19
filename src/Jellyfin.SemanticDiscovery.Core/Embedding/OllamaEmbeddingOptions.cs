namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public sealed record OllamaEmbeddingOptions
{
    public const string DefaultModel = "embeddinggemma:latest";
    public const int DefaultDimensions = 768;

    public required Uri Endpoint { get; init; }

    public string Model { get; init; } = DefaultModel;

    public int Dimensions { get; init; } = DefaultDimensions;

    public int MaximumInputsPerRequest { get; init; } = 32;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri ||
            Endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "The Ollama endpoint must be an absolute HTTP(S) URI.",
                nameof(Endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInputsPerRequest);
        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
    }
}
