using System.Text.Json.Serialization;

namespace Jellyfin.SemanticDiscovery.Extraction.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionProfile>))]
public enum ExtractionProfile
{
    Text,
    Vision,
    Hybrid,
}

public sealed record CreateExtractionRequest(
    string ManifestItemId,
    string ExpectedSourceSha256,
    ExtractionProfile ExtractionProfile);

public sealed record ExtractionAcceptedResponse(
    string JobId,
    Uri StatusUri,
    bool QueueDurable,
    string QueueDurability);

public sealed record ExtractionStatusResponse(
    string JobId,
    string ManifestItemId,
    string SourceSha256,
    ExtractionProfile ExtractionProfile,
    string Model,
    ExtractionJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Uri StatusUri,
    bool QueueDurable,
    string QueueDurability,
    string? Error,
    string? ArtifactUri);

public sealed record ExtractionCancellationResponse(
    string JobId,
    ExtractionJobState State,
    Uri StatusUri);

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionJobState>))]
public enum ExtractionJobState
{
    Pending,
    Running,
    CancellationRequested,
    Cancelled,
    Completed,
    Failed,
}

public sealed record ExtractionJob(
    string JobId,
    string ManifestItemId,
    string SourceSha256,
    ExtractionProfile ExtractionProfile,
    string Model,
    ExtractionJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    string? ArtifactPath = null)
{
    public bool IsTerminal =>
        State is ExtractionJobState.Cancelled or
            ExtractionJobState.Completed or
            ExtractionJobState.Failed;
}

public sealed record ExtractionArtifact(
    int SchemaVersion,
    string ManifestItemId,
    string SourceSha256,
    ExtractionProfile ExtractionProfile,
    string Model,
    IReadOnlyList<ExtractionArtifactChunk> Chunks);

public sealed record ExtractionArtifactChunk(
    int Ordinal,
    string Text,
    string? Heading,
    int? SourceStart,
    int? SourceEnd,
    int? PageNumber,
    IReadOnlyList<string> Provenance);
