using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.SemanticDiscovery.Setup.Showcase;

public static class ShowcaseAcquisitionEvidenceWriter
{
    public const string GeneratorId =
        "showcase-acquisition-evidence-v3";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<ShowcaseAcquisitionEvidence> WriteAsync(
        string sourceLockPath,
        string actualManifestPath,
        string phase,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (phase is not ("cold-initial" or "ordinary-initial" or
                          "validated-initial" or "warm-restart"))
        {
            throw new ArgumentException(
                "Acquisition evidence phase must be cold-initial, ordinary-initial, validated-initial, or warm-restart.",
                nameof(phase));
        }

        ShowcaseSourceLock sourceLock =
            await ShowcaseSourceLock.LoadAsync(
                sourceLockPath,
                cancellationToken).ConfigureAwait(false);
        await using FileStream actualStream =
            File.OpenRead(actualManifestPath);
        ShowcaseInstallResult actual =
            await JsonSerializer.DeserializeAsync<ShowcaseInstallResult>(
                actualStream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The showcase acquisition manifest is empty.");
        if (actual.ProfileId != sourceLock.ProfileId ||
            actual.DownloaderIdentity != sourceLock.DownloaderIdentity ||
            actual.SelectedDownloadBytes != sourceLock.SelectedDownloadBytes)
        {
            throw new InvalidDataException(
                "The acquisition manifest does not match the source lock.");
        }

        bool? expectedReused = phase switch
        {
            "cold-initial" => false,
            "validated-initial" or "warm-restart" => true,
            _ => null,
        };
        Dictionary<string, ShowcaseArtifact> lockedArtifacts =
            sourceLock.Items
                .SelectMany(static item => item.Artifacts)
                .ToDictionary(static artifact => artifact.Id, StringComparer.Ordinal);
        if (actual.Artifacts.Count != lockedArtifacts.Count ||
            actual.Artifacts.Select(static artifact => artifact.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != lockedArtifacts.Count)
        {
            throw new InvalidDataException(
                "The acquisition manifest does not contain exactly seven unique artifacts.");
        }

        ShowcaseArtifactIdentityEvidence[] artifacts = actual.Artifacts
            .OrderBy(static artifact => artifact.Id, StringComparer.Ordinal)
            .Select(artifact =>
            {
                if (!lockedArtifacts.TryGetValue(
                        artifact.Id,
                        out ShowcaseArtifact? locked) ||
                    artifact.Size != locked.Size ||
                    artifact.Sha256 != locked.Sha256 ||
                    artifact.UpstreamSha1 != locked.UpstreamSha1 ||
                    (expectedReused.HasValue &&
                     artifact.Reused != expectedReused.Value) ||
                    artifact.AllowedRedirectHosts is null ||
                    !artifact.AllowedRedirectHosts.SequenceEqual(
                        locked.AllowedRedirectHosts,
                        StringComparer.Ordinal) ||
                    !ShowcaseInstaller.ValidateTransportObservation(
                        artifact.TransportObservation,
                        locked.AllowedRedirectHosts) ||
                    !locked.AllowedTransports.Contains(
                        artifact.TransportObservation.Transport,
                        StringComparer.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Acquisition evidence for '{artifact.Id}' drifted from its lock.");
                }

                return new ShowcaseArtifactIdentityEvidence(
                    artifact.Id,
                    artifact.RelativePath,
                    artifact.Size,
                    artifact.Sha256,
                    artifact.UpstreamSha1,
                    artifact.Reused,
                    locked.AllowedTransports,
                    locked.AllowedRedirectHosts);
            })
            .ToArray();
        ShowcaseObservedTransportEvidence[]? networkObservations =
            phase == "warm-restart"
                ? null
                : actual.Artifacts
                    .Where(static artifact => !artifact.Reused)
                    .OrderBy(static artifact => artifact.Id, StringComparer.Ordinal)
                    .Select(artifact =>
                    {
                        ShowcaseArtifact locked =
                            lockedArtifacts[artifact.Id];
                        ShowcaseTransportObservation observation =
                            artifact.TransportObservation;
                        Uri effective = new(
                            observation.FinalEffectiveUrl,
                            UriKind.Absolute);
                        return new ShowcaseObservedTransportEvidence(
                            artifact.Id,
                            observation.Transport,
                            ShowcaseInstaller.GetMatchingAllowedHostRule(
                                effective.IdnHost,
                                locked.AllowedRedirectHosts),
                            observation.FinalTransferRedirectCount,
                            observation.ObservedManagedRequestHosts.Count,
                            observation.ObservedManagedRequestHosts
                                .Select(host =>
                                    ShowcaseInstaller
                                        .GetMatchingAllowedHostRule(
                                            host,
                                            locked.AllowedRedirectHosts))
                                .ToArray());
                    })
                    .ToArray();
        if (artifacts.Sum(static artifact => artifact.Size) !=
            sourceLock.SelectedDownloadBytes)
        {
            throw new InvalidDataException(
                "Acquisition evidence artifact sizes do not match the selected total.");
        }

        Dictionary<string, string> expectedGenerated =
            CreateExpectedGeneratedFiles(sourceLock);
        if (actual.GeneratedFiles.Count != expectedGenerated.Count ||
            actual.GeneratedFiles.Any(file =>
                !expectedGenerated.TryGetValue(
                    file.RelativePath,
                    out string? expectedHash) ||
                file.Sha256 != expectedHash))
        {
            throw new InvalidDataException(
                "Generated showcase files drifted from deterministic regeneration.");
        }

        ShowcaseGeneratedFile[] generatedFiles = actual.GeneratedFiles
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        ShowcaseAcquisitionEvidence evidence = new(
            SchemaVersion: 3,
            GeneratorId,
            EvidenceKind: phase switch
            {
                "cold-initial" => "cold-acquisition",
                "warm-restart" => "restart-reuse",
                _ => "ordinary-acquisition",
            },
            sourceLock.ProfileId,
            phase,
            sourceLock.DownloaderIdentity,
            SourceLockSha256:
                ShowcaseEvidenceManifestWriter
                    .ComputeCanonicalTextFileSha256(sourceLockPath),
            sourceLock.SelectedDownloadBytes,
            DownloadedArtifactCount:
                artifacts.Count(static artifact => !artifact.Reused),
            ReusedArtifactCount:
                artifacts.Count(static artifact => artifact.Reused),
            artifacts,
            generatedFiles,
            networkObservations);
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new InvalidOperationException(
                "Acquisition evidence output requires a parent directory."));
        await File.WriteAllBytesAsync(
            outputPath,
            JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    internal static Dictionary<string, string> CreateExpectedGeneratedFiles(
        ShowcaseSourceLock sourceLock)
    {
        Dictionary<string, string> expected =
            new(StringComparer.Ordinal);
        foreach (ShowcaseItem item in sourceLock.Items)
        {
            string typeFolder = item.JellyfinType == "Movie"
                ? "Movies"
                : "Books";
            string root =
                $"library/{typeFolder}/{item.DemoGenerated.FolderName}";
            string metadataName = item.JellyfinType == "Movie"
                ? "movie.nfo"
                : "metadata.opf";
            byte[] metadata = item.JellyfinType == "Movie"
                ? ShowcaseInstaller.CreateMovieNfo(item)
                : ShowcaseInstaller.CreateBookOpf(
                    item,
                    item.Artifacts.Single(static artifact =>
                        artifact.Role == "book"));
            byte[] sidecar =
                ShowcaseInstaller.CreateMetadataSidecarBytes(item);
            expected.Add(
                $"{root}/{metadataName}",
                Convert.ToHexStringLower(SHA256.HashData(metadata)));
            expected.Add(
                $"{root}/semantic-discovery.metadata.json",
                Convert.ToHexStringLower(SHA256.HashData(sidecar)));
        }

        expected.Add(
            "jellyfin-library.json",
            Convert.ToHexStringLower(SHA256.HashData(
                ShowcaseInstaller.CreateLibraryManifestBytes(sourceLock))));
        return expected;
    }
}

public sealed record ShowcaseAcquisitionEvidence(
    int SchemaVersion,
    string GeneratorId,
    string EvidenceKind,
    string ProfileId,
    string Phase,
    string DownloaderIdentity,
    string SourceLockSha256,
    long SelectedDownloadBytes,
    int DownloadedArtifactCount,
    int ReusedArtifactCount,
    IReadOnlyList<ShowcaseArtifactIdentityEvidence> Artifacts,
    IReadOnlyList<ShowcaseGeneratedFile> GeneratedFiles,
    IReadOnlyList<ShowcaseObservedTransportEvidence>? NetworkObservations);

public sealed record ShowcaseArtifactIdentityEvidence(
    string Id,
    string RelativePath,
    long Size,
    string Sha256,
    string? UpstreamSha1,
    bool Reused,
    IReadOnlyList<string> PermittedTransports,
    IReadOnlyList<string> PermittedHosts);

public sealed record ShowcaseObservedTransportEvidence(
    string Id,
    string Transport,
    string FinalEffectiveHostRule,
    int FinalTransferRedirectCount,
    int ObservedManagedHostCount,
    IReadOnlyList<string> ObservedManagedHostRules);
