using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Setup.Showcase;

public sealed record ShowcaseSourceLock
{
    internal const string ExpectedProfileId = "showcase-v1";
    internal const string ExpectedDownloaderIdentity =
        "showcase-downloader-v3-observed-transport-provenance";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public required int SchemaVersion { get; init; }

    public required string ProfileId { get; init; }

    public required string DownloaderIdentity { get; init; }

    public required string ProjectionHashContract { get; init; }

    public required long SelectedDownloadBytes { get; init; }

    public required bool DefaultEnabled { get; init; }

    public required ShowcaseRequestPolicy RequestPolicy { get; init; }

    public required ShowcasePublicationAnchorPolicy PublicationAnchorPolicy { get; init; }

    public required ShowcaseItem[] Items { get; init; }

    public required ShowcaseRejectedCandidate[] RejectedCandidates { get; init; }

    public static async ValueTask<ShowcaseSourceLock> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ShowcaseSourceLock value = await LoadUncheckedAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        value.Validate();
        return value;
    }

    internal static async ValueTask<ShowcaseSourceLock> LoadUncheckedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        return
            await JsonSerializer.DeserializeAsync<ShowcaseSourceLock>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The showcase source lock is empty.");
    }

    public void Validate()
    {
        if (SchemaVersion != 2 ||
            ProfileId != ExpectedProfileId ||
            DownloaderIdentity != ExpectedDownloaderIdentity ||
            ProjectionHashContract !=
                Jellyfin.SemanticDiscovery.Core.Projection
                    .MetadataProjectionIdentity.ContractId ||
            DefaultEnabled ||
            RequestPolicy is null ||
            RequestPolicy.Method != "GET" ||
            RequestPolicy.UserAgent != ShowcaseInstaller.UserAgent ||
            RequestPolicy.AcceptEncoding != "identity" ||
            RequestPolicy.AmbientProxy ||
            RequestPolicy.AutomaticDecompression ||
            RequestPolicy.MaximumRedirects != 5 ||
            RequestPolicy.ArtifactTimeoutSeconds != 1800 ||
            PublicationAnchorPolicy is null ||
            !PublicationAnchorPolicy.AllowOnlyListedAnchors ||
            !PublicationAnchorPolicy.RequiresExactArtifactSha256 ||
            !PublicationAnchorPolicy.RequiresExactExtractorIdentity ||
            PublicationAnchorPolicy.BookScreenshotsAllowed ||
            PublicationAnchorPolicy.ExtractedTextOrVectorsRedistributed ||
            SelectedDownloadBytes != 432_489_421 ||
            Items is null ||
            Items.Length != 6 ||
            Items.Count(static item => item.JellyfinType == "Movie") != 3 ||
            Items.Count(static item => item.JellyfinType == "Book") != 3 ||
            Items.Select(static item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != Items.Length)
        {
            throw new InvalidDataException(
                "The showcase lock must describe the exact opt-in 3 Movie + 3 Book profile.");
        }

        HashSet<string> artifactIds = new(StringComparer.Ordinal);
        long total = 0;
        foreach (ShowcaseItem item in Items)
        {
            ValidateItem(item);
            foreach (ShowcaseArtifact artifact in item.Artifacts)
            {
                if (!artifactIds.Add(artifact.Id))
                {
                    throw new InvalidDataException(
                        $"Duplicate showcase artifact '{artifact.Id}'.");
                }

                ValidateArtifact(artifact);
                total = checked(total + artifact.Size);
            }
        }

        if (total != SelectedDownloadBytes)
        {
            throw new InvalidDataException(
                "The showcase artifact sizes do not match selectedDownloadBytes.");
        }

        if (RejectedCandidates is null ||
            RejectedCandidates.Length == 0 ||
            RejectedCandidates.Any(static candidate =>
                candidate is null ||
                string.IsNullOrWhiteSpace(candidate.Id) ||
                string.IsNullOrWhiteSpace(candidate.Reason)) ||
            RejectedCandidates.Select(static candidate => candidate.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != RejectedCandidates.Length ||
            RejectedCandidates.Select(static candidate => candidate.Id)
                .Intersect(
                    Items.Select(static item => item.Id),
                    StringComparer.Ordinal)
                .Any())
        {
            throw new InvalidDataException(
                "Rejected showcase candidates must be exact, unique, and unselected.");
        }

        Dictionary<string, ShowcaseArtifact> artifacts = Items
            .SelectMany(static item => item.Artifacts)
            .ToDictionary(static artifact => artifact.Id, StringComparer.Ordinal);
        foreach (ShowcaseItem item in Items)
        {
            foreach (ShowcaseAnchor anchor in item.Anchors)
            {
                if (!artifacts.TryGetValue(
                        anchor.ArtifactId,
                        out ShowcaseArtifact? artifact) ||
                    !string.Equals(
                        anchor.ArtifactSha256,
                        artifact.Sha256,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(anchor.ExtractorIdentity) ||
                    string.IsNullOrWhiteSpace(anchor.Normalization) ||
                    string.IsNullOrWhiteSpace(anchor.Location) ||
                    string.IsNullOrWhiteSpace(anchor.Text) ||
                    (artifact.Role == "subtitle" &&
                     (anchor.CueIndex is null or <= 0 ||
                      anchor.CueStartTicks is null or < 0 ||
                      anchor.CueEndTicks is null ||
                      anchor.CueEndTicks < anchor.CueStartTicks)) ||
                    (artifact.Role != "subtitle" &&
                     (anchor.CueIndex is not null ||
                      anchor.CueStartTicks is not null ||
                      anchor.CueEndTicks is not null)))
                {
                    throw new InvalidDataException(
                        $"Showcase anchor for '{item.Id}' is not bound to its exact source and extractor.");
                }
            }

            ShowcaseEmbeddedRightsEvidence? embedded =
                item.Rights.EmbeddedEvidence;
            if (embedded is not null &&
                (!item.Artifacts.Any(artifact =>
                     artifact.Id == embedded.ArtifactId &&
                     artifact.Role == "book" &&
                     artifact.Sha256 == embedded.ArtifactSha256) ||
                 embedded.ExtractorIdentity !=
                     "epub-zip-entry-utf8-xml-dc-rights-v1" ||
                 !IsSafeZipEntryPath(embedded.EntryPath) ||
                 embedded.MaximumEntryBytes is <= 0 or > 64 * 1024 ||
                 string.IsNullOrWhiteSpace(embedded.ExpectedTitle) ||
                 string.IsNullOrWhiteSpace(embedded.ExactText)))
            {
                throw new InvalidDataException(
                    $"Embedded rights evidence for '{item.Id}' is not bound to a safe exact source entry.");
            }
        }

        ShowcaseItem sherlock = Items.Single(static item =>
            item.Id == "sherlock-holmes-1661");
        if (sherlock.Rights.EvidenceQuote !=
                "Public domain in the USA." ||
            sherlock.Rights.EmbeddedEvidence is not
            {
                ArtifactId: "sherlock-epub",
                EntryPath: "OEBPS/content.opf",
                ExtractorIdentity:
                    "epub-zip-entry-utf8-xml-dc-rights-v1",
                ExpectedTitle: "The Adventures of Sherlock Holmes",
                ExactText: "Public domain in the USA.",
            })
        {
            throw new InvalidDataException(
                "Sherlock rights evidence must be title-specific and bound to its exact EPUB OPF entry.");
        }
    }

    private static void ValidateItem(ShowcaseItem item)
    {
        if (item is null ||
            !IsSafePathSegment(item.Id) ||
            item.JellyfinType is not ("Movie" or "Book") ||
            string.IsNullOrWhiteSpace(item.Title) ||
            item.Year is < 1900 or > 2100 ||
            string.IsNullOrWhiteSpace(item.DemoGenerated.Library) ||
            string.IsNullOrWhiteSpace(item.DemoGenerated.UniqueId) ||
            !IsSafePathSegment(item.DemoGenerated.FolderName) ||
            !IsSha256(item.DemoGenerated.MetadataFileSha256) ||
            !IsSha256(item.DemoGenerated.SemanticSidecarSha256) ||
            !IsSha256(item.DemoGenerated.ProjectedMetadataSha256) ||
            string.IsNullOrWhiteSpace(item.Metadata.Overview) ||
            item.Metadata.Genres is null ||
            item.Metadata.People is null ||
            item.Metadata.Studios is null ||
            item.Artifacts is null ||
            item.Artifacts.Length == 0 ||
            item.Anchors is null ||
            string.IsNullOrWhiteSpace(item.Rights.AuthoritativeMetadataUrl) ||
            string.IsNullOrWhiteSpace(item.Rights.AuthoritativeRightsUrl) ||
            string.IsNullOrWhiteSpace(item.Rights.PrimaryLicense) ||
            item.Rights.LicenseScope is not ("uniform-project-work" or "mixed") ||
            string.IsNullOrWhiteSpace(item.Rights.Attribution) ||
            item.Rights.EvidenceRetrievedUtc == default ||
            string.IsNullOrWhiteSpace(item.Rights.EvidenceQuote) ||
            item.Rights.ReviewerState != "human-reviewed" ||
            item.Rights.Exclusions is null ||
            item.Rights.Exclusions.Length == 0 ||
            !string.Equals(
                item.Rights.EvidenceRecordSha256,
                ComputeRightsEvidenceHash(item.Rights),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Showcase item '{item?.Id}' has incomplete metadata or rights evidence.");
        }

        _ = new Uri(item.Rights.AuthoritativeMetadataUrl, UriKind.Absolute);
        _ = new Uri(item.Rights.AuthoritativeRightsUrl, UriKind.Absolute);
    }

    public static string ComputeRightsEvidenceHash(ShowcaseRights rights)
    {
        ArgumentNullException.ThrowIfNull(rights);
        StringBuilder canonical = new();
        AppendCanonical(canonical, "identity", "showcase-rights-evidence-v3");
        AppendCanonical(
            canonical,
            "authoritativeMetadataUrl",
            rights.AuthoritativeMetadataUrl);
        AppendCanonical(
            canonical,
            "authoritativeRightsUrl",
            rights.AuthoritativeRightsUrl);
        AppendCanonical(
            canonical,
            "mirrorEvidenceUrl",
            rights.MirrorEvidenceUrl);
        AppendCanonical(canonical, "primaryLicense", rights.PrimaryLicense);
        AppendCanonical(canonical, "licenseScope", rights.LicenseScope);
        AppendCanonical(canonical, "attribution", rights.Attribution);
        AppendCanonical(
            canonical,
            "evidenceRetrievedUtc",
            rights.EvidenceRetrievedUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        AppendCanonical(
            canonical,
            "evidenceQuote",
            rights.EvidenceQuote);
        AppendCanonical(canonical, "reviewerState", rights.ReviewerState);
        AppendCanonical(canonical, "evidenceNote", rights.EvidenceNote);
        AppendCanonical(
            canonical,
            "localProcessingBoundary",
            rights.LocalProcessingBoundary);
        AppendCanonical(
            canonical,
            "mirrorIsRightsAuthority",
            rights.MirrorIsRightsAuthority ? "true" : "false");
        AppendCanonical(
            canonical,
            "exclusionCount",
            rights.Exclusions.Length.ToString(CultureInfo.InvariantCulture));
        for (int index = 0; index < rights.Exclusions.Length; index++)
        {
            AppendCanonical(
                canonical,
                $"exclusion[{index}]",
                rights.Exclusions[index]);
        }
        AppendCanonical(
            canonical,
            "embeddedEvidencePresent",
            rights.EmbeddedEvidence is null ? "false" : "true");
        if (rights.EmbeddedEvidence is { } embedded)
        {
            AppendCanonical(canonical, "embeddedArtifactId", embedded.ArtifactId);
            AppendCanonical(
                canonical,
                "embeddedArtifactSha256",
                embedded.ArtifactSha256);
            AppendCanonical(canonical, "embeddedEntryPath", embedded.EntryPath);
            AppendCanonical(
                canonical,
                "embeddedExtractorIdentity",
                embedded.ExtractorIdentity);
            AppendCanonical(
                canonical,
                "embeddedMaximumEntryBytes",
                embedded.MaximumEntryBytes.ToString(
                    CultureInfo.InvariantCulture));
            AppendCanonical(
                canonical,
                "embeddedExpectedTitle",
                embedded.ExpectedTitle);
            AppendCanonical(canonical, "embeddedExactText", embedded.ExactText);
        }

        return ContentHash.Compute(canonical.ToString());
    }

    private static void ValidateArtifact(ShowcaseArtifact artifact)
    {
        if (artifact is null ||
            !IsSafePathSegment(artifact.Id) ||
            artifact.Role is not ("media" or "subtitle" or "book") ||
            !Uri.TryCreate(artifact.Url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            artifact.RetrievedUtc == default ||
            artifact.Size <= 0 ||
            !IsSha256(artifact.Sha256) ||
            !IsSafePathSegment(artifact.FileName) ||
            artifact.ContentTypes is null ||
            artifact.ContentTypes.Length == 0 ||
            artifact.AllowedRedirectHosts is null ||
            artifact.AllowedRedirectHosts.Length == 0 ||
            artifact.AllowedRedirectHosts.Any(string.IsNullOrWhiteSpace) ||
            artifact.AllowedTransports is null ||
            artifact.AllowedTransports.Length == 0 ||
            artifact.AllowedTransports.Distinct(StringComparer.Ordinal).Count() !=
                artifact.AllowedTransports.Length ||
            artifact.AllowedTransports.Any(transport =>
                !ShowcaseInstaller.SupportedTransports.Contains(transport)))
        {
            throw new InvalidDataException(
                $"Showcase artifact '{artifact?.Id}' is incomplete or unsafe.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeZipEntryPath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith('/') &&
        !value.StartsWith('\\') &&
        !value.Contains('\\') &&
        value.Split('/').All(static segment =>
            segment.Length > 0 &&
            segment is not "." and not ".." &&
            !segment.Any(char.IsControl));

    internal static bool IsSafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            Path.IsPathRooted(value) ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar) ||
            value.Contains('\\') ||
            value.Contains('/') ||
            value.EndsWith(' ') ||
            value.EndsWith('.') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Any(char.IsControl))
        {
            return false;
        }

        string stem = value.Split('.', 2)[0];
        return !WindowsDeviceNames.Contains(stem);
    }

    private static readonly HashSet<string> WindowsDeviceNames =
        new(
            [
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9",
            ],
            StringComparer.OrdinalIgnoreCase);

    private static void AppendCanonical(
        StringBuilder builder,
        string name,
        string? value)
    {
        string normalized = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormC);
        builder.Append(name);
        builder.Append(':');
        builder.Append(
            normalized.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(normalized);
        builder.Append('\n');
    }
}

public sealed record ShowcaseItem
{
    public required string Id { get; init; }

    public required string JellyfinType { get; init; }

    public required string Title { get; init; }

    public required int Year { get; init; }

    public required ShowcaseGeneratedFields DemoGenerated { get; init; }

    public required ShowcaseMetadata Metadata { get; init; }

    public required ShowcaseRights Rights { get; init; }

    public required ShowcaseArtifact[] Artifacts { get; init; }

    public required ShowcaseAnchor[] Anchors { get; init; }
}

public sealed record ShowcaseRequestPolicy
{
    public required string Method { get; init; }

    public required string UserAgent { get; init; }

    public required string AcceptEncoding { get; init; }

    public required bool AmbientProxy { get; init; }

    public required bool AutomaticDecompression { get; init; }

    public required int MaximumRedirects { get; init; }

    public required int ArtifactTimeoutSeconds { get; init; }
}

public sealed record ShowcasePublicationAnchorPolicy
{
    public required bool AllowOnlyListedAnchors { get; init; }

    public required bool RequiresExactArtifactSha256 { get; init; }

    public required bool RequiresExactExtractorIdentity { get; init; }

    public required bool BookScreenshotsAllowed { get; init; }

    public required bool ExtractedTextOrVectorsRedistributed { get; init; }
}

public sealed record ShowcaseGeneratedFields
{
    public required string Library { get; init; }

    public required string UniqueId { get; init; }

    public required string FolderName { get; init; }

    public required string MetadataFileSha256 { get; init; }

    public required string SemanticSidecarSha256 { get; init; }

    public required string ProjectedMetadataSha256 { get; init; }
}

public sealed record ShowcaseMetadata
{
    public required string Overview { get; init; }

    public string[] Genres { get; init; } = [];

    public string[] Studios { get; init; } = [];

    public string[] People { get; init; } = [];

    public string? PublicationDate { get; init; }

    public string? Publisher { get; init; }

    public string? ElectronicIsbn { get; init; }

    public string? Doi { get; init; }

    public string? QueryToMeasure { get; init; }
}

public sealed record ShowcaseRights
{
    public required string AuthoritativeMetadataUrl { get; init; }

    public required string AuthoritativeRightsUrl { get; init; }

    public string? MirrorEvidenceUrl { get; init; }

    public required string PrimaryLicense { get; init; }

    public required string LicenseScope { get; init; }

    public required string Attribution { get; init; }

    public required DateTimeOffset EvidenceRetrievedUtc { get; init; }

    public required string EvidenceQuote { get; init; }

    public required string ReviewerState { get; init; }

    public required string EvidenceRecordSha256 { get; init; }

    public string? EvidenceNote { get; init; }

    public string? LocalProcessingBoundary { get; init; }

    public required string[] Exclusions { get; init; }

    public required bool MirrorIsRightsAuthority { get; init; }

    public ShowcaseEmbeddedRightsEvidence? EmbeddedEvidence { get; init; }
}

public sealed record ShowcaseEmbeddedRightsEvidence
{
    public required string ArtifactId { get; init; }

    public required string ArtifactSha256 { get; init; }

    public required string EntryPath { get; init; }

    public required string ExtractorIdentity { get; init; }

    public required int MaximumEntryBytes { get; init; }

    public required string ExpectedTitle { get; init; }

    public required string ExactText { get; init; }
}

public sealed record ShowcaseArtifact
{
    public required string Id { get; init; }

    public required string Role { get; init; }

    public required string Url { get; init; }

    public required DateTimeOffset RetrievedUtc { get; init; }

    public required long Size { get; init; }

    public required string Sha256 { get; init; }

    public string? UpstreamSha1 { get; init; }

    public required string[] ContentTypes { get; init; }

    public required string FileName { get; init; }

    public required string[] AllowedRedirectHosts { get; init; }

    public required string[] AllowedTransports { get; init; }
}

public sealed record ShowcaseAnchor
{
    public required string ArtifactId { get; init; }

    public required string ArtifactSha256 { get; init; }

    public required string ExtractorIdentity { get; init; }

    public required string Normalization { get; init; }

    public required string Location { get; init; }

    public required string Text { get; init; }

    public int? CueIndex { get; init; }

    public long? CueStartTicks { get; init; }

    public long? CueEndTicks { get; init; }

    public string? QueryToMeasure { get; init; }
}

public sealed record ShowcaseRejectedCandidate
{
    public required string Id { get; init; }

    public required string Reason { get; init; }
}
