using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

internal static class SemanticSourceIntegrity
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static void ValidateIfPresent(
        string sourcePath,
        ContentSourceKind kind,
        ReadOnlySpan<byte> exactBytes)
    {
        string? directory = Path.GetDirectoryName(sourcePath);
        if (directory is null)
        {
            return;
        }
        string sidecarPath = Path.Combine(
            directory,
            "semantic-discovery.metadata.json");
        FileInfo file = new(sidecarPath);
        if (!file.Exists)
        {
            return;
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Semantic source integrity sidecar is a link or reparse point.");
        }

        using FileStream stream = new(
            sidecarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 64 * 1024)
        {
            throw new InvalidDataException(
                "Semantic source integrity sidecar exceeds its bound.");
        }
        byte[] sidecarBytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(sidecarBytes);
        SemanticMetadataSidecar sidecar =
            JsonSerializer.Deserialize<SemanticMetadataSidecar>(
                sidecarBytes,
                JsonOptions)
            ?? throw new InvalidDataException(
                "Semantic source integrity sidecar is empty.");
        SemanticSourceLock? expected = sidecar.ContentSources.SingleOrDefault(
            source =>
                source.Kind == kind &&
                string.Equals(
                    source.FileName,
                    Path.GetFileName(sourcePath),
                    StringComparison.Ordinal));
        if (expected is null ||
            expected.Size != exactBytes.Length ||
            !string.Equals(
                expected.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(exactBytes)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Semantic source bytes do not match their validated showcase lock.");
        }
    }
}
