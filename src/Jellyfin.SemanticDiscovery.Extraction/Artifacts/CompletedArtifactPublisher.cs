using System.Security.Cryptography;
using System.Text;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Extraction;
using Jellyfin.SemanticDiscovery.Extraction.Persistence;

namespace Jellyfin.SemanticDiscovery.Extraction.Artifacts;

public interface ICompletedArtifactPublisher
{
    ValueTask<string> PublishAsync(
        ExtractionArtifactBundle artifact,
        CancellationToken cancellationToken);
}

public sealed class FileCompletedArtifactPublisher(
    string artifactRoot,
    AtomicJsonFile atomicJsonFile) : ICompletedArtifactPublisher
{
    private readonly string _artifactRoot = Path.GetFullPath(
        string.IsNullOrWhiteSpace(artifactRoot)
            ? throw new ArgumentException(
                "An artifact root is required.",
                nameof(artifactRoot))
            : artifactRoot);

    public async ValueTask<string> PublishAsync(
        ExtractionArtifactBundle artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        string sourceSha256 = artifact.Artifact.SourceSha256.ToLowerInvariant();
        if (sourceSha256.Length != 64 ||
            sourceSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                "The completed artifact source SHA-256 is invalid.");
        }

        string path = Path.Combine(
            _artifactRoot,
            CreateArtifactFileName(
                sourceSha256,
                artifact.Artifact.ExtractionProfile.ToString(),
                artifact.Artifact.Model));
        ExtractionArtifact published = artifact.Artifact with
        {
            SourceSha256 = sourceSha256,
        };
        await atomicJsonFile.WriteAsync(
            path,
            published,
            cancellationToken).ConfigureAwait(false);
        return path;
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
}
