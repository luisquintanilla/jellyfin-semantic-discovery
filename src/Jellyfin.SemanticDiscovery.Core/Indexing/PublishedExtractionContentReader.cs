using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.Core.Indexing;

public sealed class PublishedExtractionContentReader(
    string artifactRoot,
    string extractionModel,
    string extractionMode)
    : IContentReader
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _artifactRoot = Path.GetFullPath(
        string.IsNullOrWhiteSpace(artifactRoot)
            ? throw new ArgumentException(
                "An extraction artifact root is required.",
                nameof(artifactRoot))
            : artifactRoot);
    private readonly string _extractionModel =
        string.IsNullOrWhiteSpace(extractionModel)
            ? throw new ArgumentException(
                "An extraction model is required.",
                nameof(extractionModel))
            : extractionModel;
    private readonly string _extractionMode =
        extractionMode is "Text" or "Vision" or "Hybrid"
            ? extractionMode
            : throw new ArgumentException(
                "Extraction mode must be Text, Vision, or Hybrid.",
                nameof(extractionMode));

    public bool Supports(ContentSourceKind kind) =>
        kind == ContentSourceKind.Pdf;

    public async ValueTask<IReadOnlyList<ContentChunk>> ReadAsync(
        ContentSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Supports(source.Kind))
        {
            throw new ArgumentException(
                $"Unsupported extraction source kind '{source.Kind}'.",
                nameof(source));
        }

        string sourceHash = await ComputeSha256Async(
            source.Path,
            cancellationToken).ConfigureAwait(false);
        string artifactPath = Path.GetFullPath(
            Path.Combine(
                _artifactRoot,
                CreateArtifactFileName(
                    sourceHash,
                    _extractionMode,
                    _extractionModel)));
        if (!artifactPath.StartsWith(
                _artifactRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The extraction artifact path escaped its configured root.");
        }

        if (!File.Exists(artifactPath))
        {
            throw new ContentIngestionException(
                $"Extraction artifact for '{source.Path}' is not ready.",
                source.Path,
                isTransient: true);
        }

        await using FileStream stream = new(
            artifactPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        PublishedExtraction artifact =
            await JsonSerializer.DeserializeAsync<PublishedExtraction>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Extraction artifact '{artifactPath}' is empty.");
        if (artifact.SchemaVersion != 1 ||
            !string.Equals(
                artifact.SourceSha256,
                sourceHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                artifact.Model,
                _extractionModel,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.ExtractionProfile,
                _extractionMode,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.TokenizerIdentity,
                ApplicationChunkTokenizer.IdentityHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.ChunkingIdentity,
                SemanticConstants.DocumentChunkingIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Extraction artifact '{artifactPath}' failed identity validation.");
        }

        return artifact.Chunks
            .OrderBy(static chunk => chunk.Ordinal)
            .Select(chunk =>
            {
                string contentHash = ContentHash.Compute(chunk.Text);
                return new ContentChunk
                {
                    Id = ContentChunkIdentity.Compute(
                        source.OwnerId,
                        source.Path,
                        source.Kind,
                        chunk.Ordinal,
                        contentHash),
                    OwnerId = source.OwnerId,
                    SourceKind = source.Kind,
                    SourcePath = source.Path,
                    Ordinal = chunk.Ordinal,
                    Text = chunk.Text,
                    Language = source.Language,
                    Heading = chunk.Heading,
                    SourceStart = chunk.SourceStart,
                    SourceEnd = chunk.SourceEnd,
                    PageNumber = chunk.PageNumber,
                    Provenance = chunk.Provenance,
                    ContentHash = contentHash,
                };
            })
            .ToArray();
    }

    private static async ValueTask<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string CreateArtifactFileName(
        string sourceSha256,
        string extractionMode,
        string model)
    {
        string modelHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(model)))[..16];
        return $"{sourceSha256}.{extractionMode.ToLowerInvariant()}." +
            $"{modelHash}.json";
    }

    private sealed record PublishedExtraction
    {
        public int SchemaVersion { get; init; }

        public required string SourceSha256 { get; init; }

        public required string ExtractionProfile { get; init; }

        public required string Model { get; init; }

        public required string TokenizerIdentity { get; init; }

        public required string ChunkingIdentity { get; init; }

        public required PublishedChunk[] Chunks { get; init; }
    }

    private sealed record PublishedChunk
    {
        public required int Ordinal { get; init; }

        public required string Text { get; init; }

        public string? Heading { get; init; }

        public int? SourceStart { get; init; }

        public int? SourceEnd { get; init; }

        public int? PageNumber { get; init; }

        public string[] Provenance { get; init; } = [];
    }
}
