using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Setup.Showcase;

public sealed class ShowcaseInstaller
{
    private const int MaximumRedirects = 5;
    internal static readonly HashSet<string> SupportedTransports =
        new(
            [
                "dotnet-http-pinned-dns",
                "curl-pinned-single-host",
                "curl-pinned-springer",
            ],
            StringComparer.Ordinal);
    internal const string OwnershipMarkerFileName = ".showcase-owner.json";
    internal const string UserAgent =
        "jellyfin-semantic-discovery-showcase/1.0 " +
        "(+https://github.com/luisquintanilla/jellyfin-semantic-discovery)";
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };
    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>>
        _dnsResolver;
    private readonly TimeSpan _artifactTimeout;

    public ShowcaseInstaller(
        HttpClient httpClient,
        TimeSpan? artifactTimeout = null,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? dnsResolver = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _artifactTimeout = artifactTimeout ?? TimeSpan.FromMinutes(30);
        if (_artifactTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(artifactTimeout));
        }

        _dnsResolver = dnsResolver ?? ResolveAsync;
    }

    public static HttpClient CreateHttpClient() =>
        new(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                UseProxy = false,
                ConnectCallback = ConnectPublicAsync,
            },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public async Task<ShowcaseInstallResult> InstallAsync(
        ShowcaseSourceLock sourceLock,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceLock);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        sourceLock.Validate();

        string root = Path.GetFullPath(outputRoot);
        EnsureNoReparsePoints(root);
        Directory.CreateDirectory(root);
        await EnsureOwnedRootAsync(
            root,
            sourceLock,
            cancellationToken).ConfigureAwait(false);
        RestrictDirectory(root);
        string libraryRoot = CombineContainedPath(root, "library");
        string receiptRoot = CombineContainedPath(root, ".receipts");
        EnsureNoReparsePoints(libraryRoot);
        EnsureNoReparsePoints(receiptRoot);
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(receiptRoot);
        RestrictDirectory(libraryRoot);
        RestrictDirectory(receiptRoot);
        string completionManifestPath = CombineContainedPath(
            root,
            "showcase.actual.json");
        bool completionManifestInvalidated = false;
        List<ShowcaseArtifactResult> artifacts = [];
        HashSet<string> observedManagedRequestHosts =
            new(StringComparer.OrdinalIgnoreCase);
        List<ShowcaseGeneratedFile> generated = [];

        foreach (ShowcaseItem item in sourceLock.Items)
        {
            string kindFolder = item.JellyfinType == "Movie"
                ? "Movies"
                : "Books";
            string itemDirectory = CombineContainedPath(
                libraryRoot,
                kindFolder,
                item.DemoGenerated.FolderName);
            EnsureNoReparsePoints(itemDirectory);
            Directory.CreateDirectory(itemDirectory);
            RestrictDirectory(itemDirectory);

            foreach (ShowcaseArtifact artifact in item.Artifacts)
            {
                string destination = CombineContainedPath(
                    itemDirectory,
                    artifact.FileName);
                EnsureNoReparsePoints(destination);
                string receiptPath = CombineContainedPath(
                    receiptRoot,
                    $"{artifact.Id}.json");
                EnsureNoReparsePoints(receiptPath);
                ShowcaseAcquisitionReceipt? receipt =
                    await LoadReceiptAsync(
                        receiptPath,
                        artifact,
                        cancellationToken).ConfigureAwait(false);
                bool reused = receipt is not null && await IsValidAsync(
                    destination,
                    artifact,
                    cancellationToken).ConfigureAwait(false);
                if (!reused)
                {
                    InvalidateCompletionManifest(
                        completionManifestPath,
                        ref completionManifestInvalidated);
                    File.Delete(destination);
                    File.Delete(receiptPath);
                    ShowcaseTransportObservation transport =
                        await DownloadAsync(
                        artifact,
                        destination,
                        cancellationToken).ConfigureAwait(false);
                    receipt = new ShowcaseAcquisitionReceipt(
                        sourceLock.DownloaderIdentity,
                        artifact.Id,
                        artifact.Size,
                        artifact.Sha256,
                        UserAgent,
                        "identity",
                        transport);
                    await WriteAtomicallyAsync(
                        receiptPath,
                        JsonSerializer.SerializeToUtf8Bytes(
                            receipt,
                            IndentedJsonOptions),
                        cancellationToken).ConfigureAwait(false);
                }
                foreach (string host in
                         receipt!.TransportObservation
                             .ObservedManagedRequestHosts)
                {
                    observedManagedRequestHosts.Add(host);
                }

                artifacts.Add(new ShowcaseArtifactResult(
                    artifact.Id,
                    Path.GetRelativePath(root, destination)
                        .Replace('\\', '/'),
                    artifact.Size,
                    artifact.Sha256,
                    artifact.UpstreamSha1,
                    reused,
                    receipt.TransportObservation,
                    artifact.AllowedRedirectHosts));
            }

            if (item.Rights.EmbeddedEvidence is { } embeddedEvidence)
            {
                ShowcaseArtifact evidenceArtifact = item.Artifacts.Single(
                    artifact => artifact.Id == embeddedEvidence.ArtifactId);
                await ValidateEmbeddedRightsEvidenceAsync(
                    item,
                    CombineContainedPath(
                        itemDirectory,
                        evidenceArtifact.FileName),
                    cancellationToken).ConfigureAwait(false);
            }

            string metadataPath = item.JellyfinType == "Movie"
                ? Path.Combine(itemDirectory, "movie.nfo")
                : Path.Combine(itemDirectory, "metadata.opf");
            byte[] metadata = item.JellyfinType == "Movie"
                ? CreateMovieNfo(item)
                : CreateBookOpf(
                    item,
                    item.Artifacts.Single(static artifact =>
                        artifact.Role == "book"));
            string metadataHash =
                Convert.ToHexStringLower(SHA256.HashData(metadata));
            if (!string.Equals(
                    metadataHash,
                    item.DemoGenerated.MetadataFileSha256,
                    StringComparison.Ordinal))
            {
                InvalidateCompletionManifest(
                    completionManifestPath,
                    ref completionManifestInvalidated);
                throw new InvalidDataException(
                    $"Generated metadata for '{item.Id}' does not match its lock.");
            }
            await WriteAtomicallyAsync(
                metadataPath,
                metadata,
                cancellationToken).ConfigureAwait(false);
            generated.Add(new ShowcaseGeneratedFile(
                Path.GetRelativePath(root, metadataPath).Replace('\\', '/'),
                metadataHash));
            byte[] sidecar = CreateMetadataSidecarBytes(item);
            string sidecarHash =
                Convert.ToHexStringLower(SHA256.HashData(sidecar));
            if (!string.Equals(
                    sidecarHash,
                    item.DemoGenerated.SemanticSidecarSha256,
                    StringComparison.Ordinal))
            {
                InvalidateCompletionManifest(
                    completionManifestPath,
                    ref completionManifestInvalidated);
                throw new InvalidDataException(
                    $"Generated semantic sidecar for '{item.Id}' does not match its lock.");
            }
            string sidecarPath = CombineContainedPath(
                itemDirectory,
                "semantic-discovery.metadata.json");
            await WriteAtomicallyAsync(
                sidecarPath,
                sidecar,
                cancellationToken).ConfigureAwait(false);
            generated.Add(new ShowcaseGeneratedFile(
                Path.GetRelativePath(root, sidecarPath).Replace('\\', '/'),
                sidecarHash));
        }

        string libraryManifestPath = CombineContainedPath(
            root,
            "jellyfin-library.json");
        byte[] libraryManifest = CreateLibraryManifestBytes(sourceLock);
        await WriteAtomicallyAsync(
            libraryManifestPath,
            libraryManifest,
            cancellationToken).ConfigureAwait(false);
        generated.Add(new ShowcaseGeneratedFile(
            "jellyfin-library.json",
            Convert.ToHexStringLower(SHA256.HashData(libraryManifest))));

        ShowcaseInstallResult result = new(
            sourceLock.ProfileId,
            sourceLock.DownloaderIdentity,
            sourceLock.SelectedDownloadBytes,
            artifacts,
            generated,
            observedManagedRequestHosts
                .Order(StringComparer.Ordinal)
                .ToArray());
        byte[] evidence = JsonSerializer.SerializeToUtf8Bytes(
            result,
            IndentedJsonOptions);
        await WriteAtomicallyAsync(
            completionManifestPath,
            evidence,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static async Task ValidateEmbeddedRightsEvidenceAsync(
        ShowcaseItem item,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ShowcaseEmbeddedRightsEvidence evidence =
            item.Rights.EmbeddedEvidence
            ?? throw new InvalidDataException(
                $"Showcase item '{item.Id}' has no embedded rights evidence.");
        ShowcaseArtifact artifact = item.Artifacts.Single(candidate =>
            candidate.Id == evidence.ArtifactId);
        const int maximumEvidenceArtifactBytes = 4 * 1024 * 1024;
        if (artifact.Size > maximumEvidenceArtifactBytes)
        {
            throw new InvalidDataException(
                "Embedded rights evidence must use a small bounded artifact.");
        }

        byte[] sourceBytes;
        await using (FileStream source = new(
                         artifactPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 32 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            if (source.Length != artifact.Size)
            {
                throw new InvalidDataException(
                    $"Embedded rights source for '{item.Id}' has the wrong size.");
            }

            sourceBytes = new byte[checked((int)artifact.Size)];
            await source.ReadExactlyAsync(
                sourceBytes,
                cancellationToken).ConfigureAwait(false);
            if (source.Position != source.Length ||
                !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
                    evidence.ArtifactSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Embedded rights source for '{item.Id}' failed its exact hash.");
            }
        }

        using MemoryStream sourceBuffer =
            new(sourceBytes, writable: false);
        using ZipArchive archive = new(
            sourceBuffer,
            ZipArchiveMode.Read,
            leaveOpen: false);
        ZipArchiveEntry[] matches = archive.Entries
            .Where(entry => string.Equals(
                entry.FullName,
                evidence.EntryPath,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Embedded rights entry for '{item.Id}' is missing or ambiguous.");
        }

        ZipArchiveEntry entry = matches[0];
        if (entry.Length is <= 0 ||
            entry.Length > evidence.MaximumEntryBytes ||
            entry.CompressedLength <= 0 ||
            entry.Length > checked(entry.CompressedLength * 100))
        {
            throw new InvalidDataException(
                $"Embedded rights entry for '{item.Id}' exceeds its expansion bound.");
        }

        byte[] entryBytes = new byte[checked((int)entry.Length)];
        await using (Stream entryStream = entry.Open())
        {
            await entryStream.ReadExactlyAsync(
                entryBytes,
                cancellationToken).ConfigureAwait(false);
            if (entryStream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"Embedded rights entry for '{item.Id}' exceeded its declared size.");
            }
        }

        string xml = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(entryBytes);
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = evidence.MaximumEntryBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using StringReader text = new(xml);
        using XmlReader reader = XmlReader.Create(text, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        string[] rightsValues = document.Descendants(dc + "rights")
            .Select(static element => element.Value)
            .ToArray();
        string[] titleValues = document.Descendants(dc + "title")
            .Select(static element => element.Value)
            .ToArray();

        if (titleValues.Length != 1 ||
            !string.Equals(
                titleValues[0],
                evidence.ExpectedTitle,
                StringComparison.Ordinal) ||
            rightsValues.Length != 1 ||
            !string.Equals(
                rightsValues[0],
                evidence.ExactText,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Embedded rights text for '{item.Id}' drifted from its lock.");
        }
    }

    private static void InvalidateCompletionManifest(
        string completionManifestPath,
        ref bool invalidated)
    {
        if (invalidated)
        {
            return;
        }

        EnsureNoReparsePoints(completionManifestPath);
        File.Delete(completionManifestPath);
        invalidated = true;
    }

    private static async Task EnsureOwnedRootAsync(
        string root,
        ShowcaseSourceLock sourceLock,
        CancellationToken cancellationToken)
    {
        string markerPath = CombineContainedPath(
            root,
            OwnershipMarkerFileName);
        EnsureNoReparsePoints(markerPath);
        if (File.Exists(markerPath))
        {
            ValidateOwnershipMarker(markerPath, sourceLock);
            return;
        }

        string legacyMarkerPath = CombineContainedPath(
            root,
            "showcase.actual.json");
        EnsureNoReparsePoints(legacyMarkerPath);
        string[] existingEntries = Directory.GetFileSystemEntries(root);
        if (existingEntries.Length != 0 &&
            !IsValidLegacyMarker(legacyMarkerPath, sourceLock))
        {
            throw new InvalidDataException(
                "Showcase acquisition refused a non-empty unowned output directory.");
        }

        ShowcaseOwnershipMarker marker = new(
            SchemaVersion: 1,
            sourceLock.ProfileId,
            sourceLock.DownloaderIdentity);
        await WriteAtomicallyAsync(
            markerPath,
            JsonSerializer.SerializeToUtf8Bytes(
                marker,
                IndentedJsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOwnershipMarker(
        string markerPath,
        ShowcaseSourceLock sourceLock)
    {
        ShowcaseOwnershipMarker marker;
        try
        {
            marker =
                JsonSerializer.Deserialize<ShowcaseOwnershipMarker>(
                    File.ReadAllBytes(markerPath))
                ?? throw new InvalidDataException(
                    "The showcase ownership marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The showcase ownership marker is invalid.",
                exception);
        }

        if (marker.SchemaVersion != 1 ||
            marker.ProfileId != sourceLock.ProfileId ||
            marker.DownloaderIdentity != sourceLock.DownloaderIdentity)
        {
            throw new InvalidDataException(
                "The showcase ownership marker does not match the source lock.");
        }
    }

    private static bool IsValidLegacyMarker(
        string markerPath,
        ShowcaseSourceLock sourceLock)
    {
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            using JsonDocument marker =
                JsonDocument.Parse(File.ReadAllBytes(markerPath));
            return marker.RootElement.TryGetProperty(
                    "ProfileId",
                    out JsonElement profile) &&
                profile.GetString() == sourceLock.ProfileId &&
                marker.RootElement.TryGetProperty(
                    "DownloaderIdentity",
                    out JsonElement downloader) &&
                downloader.GetString() == sourceLock.DownloaderIdentity;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal async Task<ShowcaseTransportObservation> DownloadAsync(
        ShowcaseArtifact artifact,
        string destination,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_artifactTimeout);
        Uri current = new(artifact.Url, UriKind.Absolute);
        CookieContainer? springerCookies =
            artifact.Id == "digital-humanism-pdf"
                ? new CookieContainer()
                : null;
        List<string> observedManagedRequestHosts = [];
        string temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        EnsureSameVolume(destination, temporaryPath);
        try
        {
            for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                IPAddress[] resolvedAddresses = await ValidateUriAsync(
                    current,
                    artifact.AllowedRedirectHosts,
                    timeout.Token).ConfigureAwait(false);
                observedManagedRequestHosts.Add(current.IdnHost);
                using HttpRequestMessage request =
                    new(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.ParseAdd(
                    string.Join(
                        ", ",
                        artifact.ContentTypes.Select(static (type, index) =>
                            index == 0
                                ? type
                                : $"{type};q=0.{Math.Max(1, 9 - index)}")) +
                    ", */*;q=0.1");
                request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
                request.Headers.AcceptEncoding.ParseAdd("identity");
                string? cookie = springerCookies?.GetCookieHeader(current);
                if (!string.IsNullOrEmpty(cookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);
                }
                using HttpResponseMessage response =
                    await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                if (springerCookies is not null &&
                    response.Headers.TryGetValues(
                        "Set-Cookie",
                        out IEnumerable<string>? cookies))
                {
                    foreach (string value in cookies)
                    {
                        springerCookies.SetCookies(current, value);
                    }
                }
                if (response.StatusCode is
                        HttpStatusCode.Forbidden or
                        HttpStatusCode.TooManyRequests &&
                    artifact.Id is
                        "ai-for-teachers-epub" or
                        "spring-video" or
                        "coffee-run-video")
                {
                    CurlTransferResult curl = await DownloadSingleWithCurlAsync(
                        artifact,
                        current,
                        temporaryPath,
                        destination,
                        resolvedAddresses,
                        timeout.Token).ConfigureAwait(false);
                    return new ShowcaseTransportObservation(
                        "curl-pinned-single-host",
                        curl.FinalEffectiveUrl,
                        curl.RedirectCount,
                        observedManagedRequestHosts
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order(StringComparer.Ordinal)
                            .ToArray());
                }
                if (artifact.Id == "digital-humanism-pdf" &&
                    response.StatusCode == HttpStatusCode.OK &&
                    (response.Content.Headers.ContentLength != artifact.Size ||
                     !string.Equals(
                         response.Content.Headers.ContentType?.MediaType,
                         "application/pdf",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    CurlTransferResult curl =
                        await DownloadSpringerWithCurlAsync(
                        artifact,
                        temporaryPath,
                        destination,
                        timeout.Token).ConfigureAwait(false);
                    return new ShowcaseTransportObservation(
                        "curl-pinned-springer",
                        curl.FinalEffectiveUrl,
                        curl.RedirectCount,
                        observedManagedRequestHosts
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order(StringComparer.Ordinal)
                            .ToArray());
                }

                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == MaximumRedirects ||
                        response.Headers.Location is null)
                    {
                        throw new HttpRequestException(
                            $"Artifact '{artifact.Id}' exceeded the redirect limit.");
                    }

                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                ValidateResponseHeaders(response, artifact);
                await using Stream source =
                    await response.Content.ReadAsStreamAsync(timeout.Token)
                        .ConfigureAwait(false);
                await using (FileStream target = CreateTemporaryFile(
                                 temporaryPath))
                {
                    string hash = await CopyAndHashAsync(
                        source,
                        target,
                        artifact.Size,
                        timeout.Token).ConfigureAwait(false);
                    if (!string.Equals(
                            hash,
                            artifact.Sha256,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Artifact '{artifact.Id}' failed SHA-256 validation.");
                    }

                    await target.FlushAsync(timeout.Token).ConfigureAwait(false);
                    target.Flush(flushToDisk: true);
                }

                timeout.Token.ThrowIfCancellationRequested();
                File.Move(temporaryPath, destination, overwrite: true);
                return new ShowcaseTransportObservation(
                    "dotnet-http-pinned-dns",
                    current.AbsoluteUri,
                    redirect,
                    observedManagedRequestHosts
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.Ordinal)
                        .ToArray());
            }

            throw new InvalidOperationException("Unreachable redirect state.");
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static async ValueTask<ShowcaseAcquisitionReceipt?> LoadReceiptAsync(
        string path,
        ShowcaseArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            ShowcaseAcquisitionReceipt? receipt =
                await JsonSerializer.DeserializeAsync<ShowcaseAcquisitionReceipt>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            return receipt is not null &&
                receipt.DownloaderIdentity ==
                    ShowcaseSourceLock.ExpectedDownloaderIdentity &&
                receipt.ArtifactId == artifact.Id &&
                receipt.Size == artifact.Size &&
                receipt.Sha256 == artifact.Sha256 &&
                receipt.UserAgent == UserAgent &&
                receipt.AcceptEncoding == "identity" &&
                artifact.AllowedTransports.Contains(
                    receipt.TransportObservation.Transport,
                    StringComparer.Ordinal) &&
                ValidateTransportObservation(
                    receipt.TransportObservation,
                    artifact.AllowedRedirectHosts)
                    ? receipt
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal async ValueTask<IPAddress[]> ValidateUriAsync(
        Uri uri,
        IReadOnlyList<string> allowedHosts,
        CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port != 443 ||
            !IsHostAllowed(uri.IdnHost, allowedHosts))
        {
            throw new InvalidDataException(
                "Showcase acquisition URL is outside its HTTPS host allowlist.");
        }

        IPAddress[] addresses =
            await _dnsResolver(uri.IdnHost, cancellationToken)
                .ConfigureAwait(false);
        if (addresses.Length == 0 ||
            addresses.Any(static address => !IsPublicAddress(address)))
        {
            throw new InvalidDataException(
                "Showcase acquisition host did not resolve exclusively to public addresses.");
        }

        return addresses;
    }

    private static async Task<CurlTransferResult> DownloadSingleWithCurlAsync(
        ShowcaseArtifact artifact,
        Uri uri,
        string temporaryPath,
        string destination,
        IPAddress[] resolvedAddresses,
        CancellationToken cancellationToken)
    {
        await using (FileStream placeholder =
                     CreateTemporaryFile(temporaryPath))
        {
            await placeholder.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        ProcessStartInfo start = new()
        {
            FileName = "curl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
                 {
                     "--fail",
                     "--silent",
                     "--show-error",
                     "--retry",
                     "4",
                     "--retry-all-errors",
                     "--retry-delay",
                     "5",
                     "--retry-max-time",
                     "60",
                     "--request",
                     "GET",
                     "--proto",
                     "=https",
                     "--max-redirs",
                     "0",
                     "--max-filesize",
                     artifact.Size.ToString(
                         System.Globalization.CultureInfo.InvariantCulture),
                     "--noproxy",
                     "*",
                     "--user-agent",
                     UserAgent,
                     "--header",
                     $"Accept: {string.Join(", ", artifact.ContentTypes)}, */*;q=0.1",
                     "--header",
                     "Accept-Language: en-US,en;q=0.8",
                     "--header",
                     "Accept-Encoding: identity",
                     "--resolve",
                     $"{uri.IdnHost}:443:{FormatCurlAddress(resolvedAddresses[0])}",
                     "--output",
                     temporaryPath,
                     "--write-out",
                     "%{http_code}\\n%{content_type}\\n%{size_download}\\n%{url_effective}\\n%{num_redirects}",
                     uri.AbsoluteUri,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Could not start the required curl showcase transport.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }

        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new HttpRequestException(
                $"The locked single-host GET failed through curl (exit {process.ExitCode}, {error.Length} diagnostic characters).");
        }

        string[] values = output.Split(
            '\n',
            StringSplitOptions.TrimEntries);
        if (values.Length < 5 ||
            values[0] != "200" ||
            !artifact.ContentTypes.Any(type =>
                values[1].StartsWith(
                    type,
                    StringComparison.OrdinalIgnoreCase)) ||
            values[2] != artifact.Size.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ||
            !Uri.TryCreate(values[3], UriKind.Absolute, out Uri? effective) ||
            effective != uri ||
            !int.TryParse(
                values[4],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int redirectCount) ||
            redirectCount != 0 ||
            !await IsValidAsync(
                temporaryPath,
                artifact,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "The locked single-host curl response failed URL, type, size, or hash validation.");
        }

        File.Move(temporaryPath, destination, overwrite: true);
        return new CurlTransferResult(
            effective.AbsoluteUri,
            redirectCount);
    }

    private async Task<CurlTransferResult> DownloadSpringerWithCurlAsync(
        ShowcaseArtifact artifact,
        string temporaryPath,
        string destination,
        CancellationToken cancellationToken)
    {
        string[] hosts = ["link.springer.com", "idp.springer.com"];
        Dictionary<string, IPAddress> addresses =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string host in hosts)
        {
            IPAddress[] resolved =
                await _dnsResolver(host, cancellationToken)
                    .ConfigureAwait(false);
            IPAddress? address = resolved.FirstOrDefault(
                static candidate =>
                    IsPublicAddress(candidate) &&
                    candidate.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
            {
                throw new InvalidDataException(
                    $"Springer host '{host}' has no validated public IPv4 address.");
            }

            addresses.Add(host, address);
        }

        string cookiePath = $"{temporaryPath}.cookies";
        try
        {
            await using (FileStream placeholder =
                         CreateTemporaryFile(temporaryPath))
            {
                await placeholder.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            await using (FileStream cookie =
                         CreateTemporaryFile(cookiePath))
            {
                await cookie.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            ProcessStartInfo start = new()
            {
                FileName = "curl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            List<string> arguments =
            [
                "--fail",
                "--silent",
                "--show-error",
                "--retry",
                "4",
                "--retry-all-errors",
                "--retry-delay",
                "5",
                "--retry-max-time",
                "60",
                "--request",
                "GET",
                "--proto",
                "=https",
                "--proto-redir",
                "=https",
                "--location",
                "--max-redirs",
                MaximumRedirects.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--max-time",
                ((int)_artifactTimeout.TotalSeconds).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--max-filesize",
                artifact.Size.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--noproxy",
                "*",
                "--user-agent",
                UserAgent,
                "--header",
                "Accept: application/pdf, application/octet-stream;q=0.8, */*;q=0.1",
                "--header",
                "Accept-Language: en-US,en;q=0.8",
                "--header",
                "Accept-Encoding: identity",
                "--cookie",
                cookiePath,
                "--cookie-jar",
                cookiePath,
                "--output",
                temporaryPath,
                "--write-out",
                "%{http_code}\\n%{content_type}\\n%{size_download}\\n%{url_effective}\\n%{num_redirects}",
            ];
            foreach ((string host, IPAddress address) in addresses)
            {
                arguments.Add("--connect-to");
                arguments.Add(
                    $"{host}:443:{FormatCurlAddress(address)}:443");
            }
            arguments.Add("--connect-to");
            arguments.Add("::127.0.0.1:1");
            arguments.Add(artifact.Url);
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException(
                    "Could not start the required Springer curl transport.");
            Task<string> outputTask =
                process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask =
                process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }

                throw;
            }

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new HttpRequestException(
                    $"The locked Springer GET failed through curl (exit {process.ExitCode}, {error.Length} diagnostic characters).");
            }

            string[] values = output.Split(
                '\n',
                StringSplitOptions.TrimEntries);
            if (values.Length < 5 ||
                values[0] != "200" ||
                !values[1].StartsWith(
                    "application/pdf",
                    StringComparison.OrdinalIgnoreCase) ||
                values[2] != artifact.Size.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ||
                !Uri.TryCreate(
                    values[3],
                    UriKind.Absolute,
                    out Uri? effective) ||
                effective.Scheme != Uri.UriSchemeHttps ||
                !IsHostAllowed(
                    effective.IdnHost,
                    artifact.AllowedRedirectHosts) ||
                !int.TryParse(
                    values[4],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int redirectCount) ||
                redirectCount > MaximumRedirects ||
                !await IsValidAsync(
                    temporaryPath,
                    artifact,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "The locked Springer curl response failed redirect, URL, type, size, or hash validation.");
            }

            File.Move(temporaryPath, destination, overwrite: true);
            return new CurlTransferResult(
                effective.AbsoluteUri,
                redirectCount);
        }
        finally
        {
            File.Delete(cookiePath);
        }
    }

    private static string FormatCurlAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 ||
            addresses.Any(static address => !IsPublicAddress(address)))
        {
            throw new InvalidDataException(
                "The HTTP connection target did not resolve exclusively to public addresses.");
        }

        Exception? lastFailure = null;
        foreach (IPAddress address in addresses)
        {
            Socket socket = new(
                address.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception)
                when (exception is SocketException or IOException)
            {
                socket.Dispose();
                lastFailure = exception;
            }
        }

        throw new HttpRequestException(
            "Could not connect to any validated public source address.",
            lastFailure);
    }

    internal static bool IsHostAllowed(
        string host,
        IReadOnlyList<string> allowedHosts) =>
        allowedHosts.Any(rule =>
            rule[0] == '.'
                ? host.Length > rule.Length &&
                  host.EndsWith(rule, StringComparison.OrdinalIgnoreCase)
                : string.Equals(
                    host,
                    rule,
                    StringComparison.OrdinalIgnoreCase));

    internal static bool ValidateTransportObservation(
        ShowcaseTransportObservation? observation,
        IReadOnlyList<string> allowedHosts)
    {
        if (observation is null ||
            !SupportedTransports.Contains(observation.Transport) ||
            observation.FinalTransferRedirectCount is < 0 or > MaximumRedirects ||
            !Uri.TryCreate(
                observation.FinalEffectiveUrl,
                UriKind.Absolute,
                out Uri? effective) ||
            effective.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(effective.UserInfo) ||
            !IsHostAllowed(effective.IdnHost, allowedHosts) ||
            observation.ObservedManagedRequestHosts is null ||
            observation.ObservedManagedRequestHosts
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() !=
                observation.ObservedManagedRequestHosts.Count ||
            observation.ObservedManagedRequestHosts.Any(host =>
                !IsHostAllowed(host, allowedHosts)))
        {
            return false;
        }

        return observation.Transport != "dotnet-http-pinned-dns" ||
            observation.ObservedManagedRequestHosts.Count > 0;
    }

    internal static string GetMatchingAllowedHostRule(
        string host,
        IReadOnlyList<string> allowedHosts) =>
        allowedHosts
            .Where(rule => IsHostAllowed(host, [rule]))
            .OrderByDescending(static rule => rule[0] != '.')
            .ThenByDescending(static rule => rule.Length)
            .FirstOrDefault()
        ?? throw new InvalidDataException(
            $"Observed host '{host}' is outside its permitted host rules.");

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily ==
            System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xE0) == 0x20 &&
                !IsPrefix(bytes, [0x20, 0x01, 0x0D, 0xB8]) &&
                !IsPrefix(bytes, [0x20, 0x01, 0x00, 0x00]) &&
                !IsPrefix(bytes, [0x20, 0x02]);
        }

        return bytes[0] != 0 &&
            bytes[0] != 10 &&
            bytes[0] != 127 &&
            !(bytes[0] == 169 && bytes[1] == 254) &&
            !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) &&
            !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) &&
            !(bytes[0] == 192 && bytes[1] == 168) &&
            !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
            !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
            !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) &&
            bytes[0] < 224;
    }

    private static bool IsPrefix(
        ReadOnlySpan<byte> address,
        ReadOnlySpan<byte> prefix) =>
        address.StartsWith(prefix);

    internal static async ValueTask<string> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int maximumRead = checked((int)Math.Min(
                buffer.Length,
                expectedSize - total + 1));
            int read = await source.ReadAsync(
                buffer.AsMemory(0, maximumRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (total + read > expectedSize)
            {
                throw new InvalidDataException(
                    "Showcase artifact exceeded its locked byte length.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
            total += read;
        }

        if (total != expectedSize)
        {
            throw new InvalidDataException(
                "Showcase artifact was shorter than its locked byte length.");
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static async ValueTask<bool> IsValidAsync(
        string path,
        ShowcaseArtifact artifact,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length != artifact.Size)
        {
            return false;
        }

        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexStringLower(hash),
            artifact.Sha256,
            StringComparison.Ordinal);
    }

    private static void ValidateResponseHeaders(
        HttpResponseMessage response,
        ShowcaseArtifact artifact)
    {
        if (response.Content.Headers.ContentLength is { } length &&
            length != artifact.Size)
        {
            throw new InvalidDataException(
                $"Artifact '{artifact.Id}' from '{response.RequestMessage?.RequestUri?.IdnHost}' declared {length} bytes with type '{response.Content.Headers.ContentType?.MediaType}'; expected {artifact.Size}.");
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null ||
            !artifact.ContentTypes.Contains(
                mediaType,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Artifact '{artifact.Id}' returned an unexpected content type.");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static FileStream CreateTemporaryFile(string path)
    {
        FileStreamOptions options = new()
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            BufferSize = 128 * 1024,
            Options =
                FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        EnsureNoReparsePoints(path);
        EnsureNoReparsePoints(temporaryPath);
        EnsureSameVolume(path, temporaryPath);
        try
        {
            await using (FileStream stream = CreateTemporaryFile(temporaryPath))
            {
                await stream.WriteAsync(content, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static byte[] CreateMovieNfo(ShowcaseItem item) =>
        CreateXml(writer =>
        {
            writer.WriteStartElement("movie");
            writer.WriteElementString("title", item.Title);
            writer.WriteElementString(
                "year",
                item.Year.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteElementString("plot", item.Metadata.Overview);
            foreach (string genre in item.Metadata.Genres)
            {
                writer.WriteElementString("genre", genre);
            }
            foreach (string studio in item.Metadata.Studios)
            {
                writer.WriteElementString("studio", studio);
            }
            foreach (string person in item.Metadata.People)
            {
                writer.WriteElementString("director", person);
            }
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "showcase-demo-generated");
            writer.WriteAttributeString("default", "true");
            writer.WriteString(item.DemoGenerated.UniqueId);
            writer.WriteEndElement();
            writer.WriteEndElement();
        });

    internal static byte[] CreateBookOpf(
        ShowcaseItem item,
        ShowcaseArtifact artifact) =>
        CreateXml(writer =>
        {
            writer.WriteStartElement(
                "package",
                "http://www.idpf.org/2007/opf");
            writer.WriteAttributeString("unique-identifier", "BookId");
            writer.WriteAttributeString("version", "2.0");
            writer.WriteStartElement("metadata");
            writer.WriteAttributeString(
                "xmlns",
                "dc",
                null,
                "http://purl.org/dc/elements/1.1/");
            writer.WriteStartElement(
                "dc",
                "identifier",
                "http://purl.org/dc/elements/1.1/");
            writer.WriteAttributeString("id", "BookId");
            writer.WriteString(item.DemoGenerated.UniqueId);
            writer.WriteEndElement();
            writer.WriteElementString(
                "dc",
                "title",
                "http://purl.org/dc/elements/1.1/",
                item.Title);
            foreach (string person in item.Metadata.People)
            {
                writer.WriteElementString(
                    "dc",
                    "creator",
                    "http://purl.org/dc/elements/1.1/",
                    person);
            }
            writer.WriteElementString(
                "dc",
                "date",
                "http://purl.org/dc/elements/1.1/",
                item.Year.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteElementString(
                "dc",
                "description",
                "http://purl.org/dc/elements/1.1/",
                item.Metadata.Overview);
            writer.WriteEndElement();
            writer.WriteStartElement("manifest");
            writer.WriteStartElement("item");
            writer.WriteAttributeString("id", "book");
            writer.WriteAttributeString("href", artifact.FileName);
            writer.WriteAttributeString(
                "media-type",
                artifact.FileName.EndsWith(
                    ".epub",
                    StringComparison.OrdinalIgnoreCase)
                    ? "application/epub+zip"
                    : "application/pdf");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        });

    internal static string CreateProjectedMetadata(ShowcaseItem item)
    {
        SemanticMetadataSidecar sidecar = CreateMetadataSidecar(item);
        MetadataRecord record = new()
        {
            Id = Guid.Empty,
            Kind = sidecar.Kind,
            ItemType = sidecar.ItemType,
            MediaType = sidecar.MediaType,
            Title = sidecar.Title,
            Overview = sidecar.Overview,
            ProductionYear = sidecar.ProductionYear,
            LibraryId = Guid.Empty,
            ParentId = Guid.Empty,
            Genres = sidecar.Genres,
            Tags = sidecar.Tags,
            People = sidecar.People,
            Studios = sidecar.Studios,
        };
        return MetadataProjectionIdentity.CreateCanonicalText(record);
    }

    internal static SemanticMetadataSidecar CreateMetadataSidecar(
        ShowcaseItem item) => new()
        {
            SchemaVersion = 2,
            ProjectionHashContract =
                MetadataProjectionIdentity.ContractId,
            Kind = item.JellyfinType == "Movie"
                ? MediaKind.Movie
                : MediaKind.Book,
            ItemType = item.JellyfinType,
            MediaType = item.JellyfinType == "Movie"
                ? "Video"
                : "Book",
            Title = item.Title,
            ProductionYear = item.Year,
            Overview = item.Metadata.Overview,
            Genres = item.Metadata.Genres,
            Tags = [],
            People = item.Metadata.People,
            Studios = item.Metadata.Studios,
            ProjectedMetadataSha256 =
                item.DemoGenerated.ProjectedMetadataSha256,
            ContentSources = item.Artifacts
                .Where(static artifact =>
                    artifact.Role is "book" or "subtitle")
                .Select(artifact => new SemanticSourceLock
                {
                    FileName = artifact.FileName,
                    Kind = artifact.Role == "subtitle"
                        ? ContentSourceKind.Subtitle
                        : artifact.FileName.EndsWith(
                            ".epub",
                            StringComparison.OrdinalIgnoreCase)
                            ? ContentSourceKind.Epub
                            : ContentSourceKind.Pdf,
                    Size = artifact.Size,
                    Sha256 = artifact.Sha256,
                })
                .OrderBy(static source => source.FileName, StringComparer.Ordinal)
                .ToArray(),
        };

    internal static byte[] CreateMetadataSidecarBytes(
        ShowcaseItem item) =>
        JsonSerializer.SerializeToUtf8Bytes(
            CreateMetadataSidecar(item),
            IndentedJsonOptions);

    private static byte[] CreateXml(Action<XmlWriter> write)
    {
        using MemoryStream buffer = new();
        XmlWriterSettings settings = new()
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        };
        using (XmlWriter writer = XmlWriter.Create(buffer, settings))
        {
            write(writer);
        }

        return buffer.ToArray();
    }

    private static ShowcaseLibraryManifest CreateLibraryManifest(
        ShowcaseSourceLock sourceLock)
    {
        ShowcaseLibrary[] libraries = sourceLock.Items
            .GroupBy(static item => item.DemoGenerated.Library)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                ShowcaseItem first = group.First();
                string folder = first.JellyfinType == "Movie"
                    ? "Movies"
                    : "Books";
                return new ShowcaseLibrary(
                    group.Key,
                    first.JellyfinType == "Movie" ? "movies" : "books",
                    first.JellyfinType,
                    $"/media/{folder}",
                    group.OrderBy(static item => item.Title, StringComparer.Ordinal)
                        .Select(item =>
                        {
                            ShowcaseArtifact primary = item.Artifacts.Single(
                                static artifact => artifact.Role is "media" or "book");
                            return new ShowcaseLibraryItem(
                                item.Title,
                                item.JellyfinType,
                                $"/media/{folder}/{item.DemoGenerated.FolderName}/{primary.FileName}",
                                item.JellyfinType == "Movie"
                                    ? item.Metadata.Overview
                                    : null,
                                item.JellyfinType == "Movie"
                                    ? item.Metadata.Genres
                                    : null,
                                item.JellyfinType == "Movie" ? [] : null,
                                item.JellyfinType == "Movie"
                                    ? item.Metadata.Studios
                                    : null,
                                null);
                        })
                        .ToArray());
            })
            .ToArray();
        return new ShowcaseLibraryManifest(sourceLock.ProfileId, libraries);
    }

    internal static byte[] CreateLibraryManifestBytes(
        ShowcaseSourceLock sourceLock) =>
        JsonSerializer.SerializeToUtf8Bytes(
            CreateLibraryManifest(sourceLock),
            IndentedJsonOptions);

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        FileSystemInfo? current = File.Exists(fullPath)
            ? new FileInfo(fullPath)
            : Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new DirectoryInfo(
                    Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException(
                        "Showcase paths require a parent directory."));
        while (current is not null)
        {
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Showcase path component '{current.FullName}' is a link or reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }

    internal static void EnsureSameVolume(string destination, string temporary)
    {
        if (!string.Equals(
                Path.GetPathRoot(Path.GetFullPath(destination)),
                Path.GetPathRoot(Path.GetFullPath(temporary)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Showcase temporary and final files must share a filesystem volume.");
        }
    }

    internal static string CombineContainedPath(
        string root,
        params string[] segments)
    {
        string fullRoot = Path.GetFullPath(root);
        string candidate = fullRoot;
        foreach (string segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        candidate = Path.GetFullPath(candidate);
        string prefix = fullRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A showcase path escaped its owned root.");
        }

        return candidate;
    }

    private static async ValueTask<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);
}

public sealed record ShowcaseInstallResult(
    string ProfileId,
    string DownloaderIdentity,
    long SelectedDownloadBytes,
    IReadOnlyList<ShowcaseArtifactResult> Artifacts,
    IReadOnlyList<ShowcaseGeneratedFile> GeneratedFiles,
    IReadOnlyList<string> ObservedManagedRequestHosts);

internal sealed record ShowcaseOwnershipMarker(
    int SchemaVersion,
    string ProfileId,
    string DownloaderIdentity);

public sealed record ShowcaseArtifactResult(
    string Id,
    string RelativePath,
    long Size,
    string Sha256,
    string? UpstreamSha1,
    bool Reused,
    ShowcaseTransportObservation TransportObservation,
    IReadOnlyList<string> AllowedRedirectHosts);

public sealed record ShowcaseAcquisitionReceipt(
    string DownloaderIdentity,
    string ArtifactId,
    long Size,
    string Sha256,
    string UserAgent,
    string AcceptEncoding,
    ShowcaseTransportObservation TransportObservation);

public sealed record ShowcaseTransportObservation(
    string Transport,
    string FinalEffectiveUrl,
    int FinalTransferRedirectCount,
    IReadOnlyList<string> ObservedManagedRequestHosts);

internal sealed record CurlTransferResult(
    string FinalEffectiveUrl,
    int RedirectCount);

public sealed record ShowcaseGeneratedFile(
    string RelativePath,
    string Sha256);

public sealed record ShowcaseLibraryManifest(
    string ProfileId,
    ShowcaseLibrary[] Libraries);

public sealed record ShowcaseLibrary(
    string Name,
    string CollectionType,
    string ItemType,
    string Path,
    ShowcaseLibraryItem[] Items);

public sealed record ShowcaseLibraryItem(
    string Title,
    string Type,
    string Path,
    string? Overview,
    string[]? Genres,
    string[]? Tags,
    string[]? Studios,
    string? OfficialRating);
