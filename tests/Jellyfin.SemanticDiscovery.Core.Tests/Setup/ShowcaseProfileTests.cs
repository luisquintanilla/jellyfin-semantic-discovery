using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Setup.Showcase;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Setup;

public sealed class ShowcaseProfileTests : IDisposable
{
    private static readonly string[] AllowedCommittedExtensions =
        [".json", ".md"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"showcase-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CommittedSourceLockIsCompleteAndMatchesLiteralSelectedBytes()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();

        Assert.Equal(432_489_421, sourceLock.SelectedDownloadBytes);
        Assert.Equal(
            [
                "ai-for-teachers-2024",
                "coffee-run-2020",
                "digital-humanism-2024",
                "sherlock-holmes-1661",
                "sintel-2010",
                "spring-2019",
            ],
            sourceLock.Items
                .Select(static item => item.Id)
                .Order(StringComparer.Ordinal));
        Assert.All(
            sourceLock.Items,
            item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(
                    item.Rights.AuthoritativeMetadataUrl));
                Assert.False(string.IsNullOrWhiteSpace(
                    item.Rights.AuthoritativeRightsUrl));
                Assert.False(string.IsNullOrWhiteSpace(
                    item.Rights.Attribution));
                Assert.NotEmpty(item.Rights.Exclusions);
            });
        Assert.Contains(
            sourceLock.Items.SelectMany(static item => item.Artifacts),
            artifact =>
                artifact.Sha256 ==
                "24820089e5882c604eec07d6a2382ab28d28a951909aea29b0ab63fb63d80299");
        Assert.NotEmpty(sourceLock.RejectedCandidates);
        Assert.Equal(
            sourceLock.RejectedCandidates.Length,
            sourceLock.RejectedCandidates
                .Select(static candidate => candidate.Id)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Empty(
            sourceLock.RejectedCandidates
                .Select(static candidate => candidate.Id)
                .Intersect(
                    sourceLock.Items.Select(static item => item.Id),
                    StringComparer.Ordinal));
        Assert.DoesNotContain(
            sourceLock.RejectedCandidates,
            static candidate => candidate.Id.Contains(
                "Gutenberg",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SourceLockRejectsMissingRightsAndAnchorHashDrift()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        ShowcaseItem first = sourceLock.Items[0];
        ShowcaseSourceLock missingRights = sourceLock with
        {
            Items =
            [
                first with
                {
                    Rights = first.Rights with { Attribution = string.Empty },
                },
                .. sourceLock.Items[1..],
            ],
        };
        ShowcaseAnchor anchor = first.Anchors[0];
        ShowcaseSourceLock anchorDrift = sourceLock with
        {
            Items =
            [
                first with
                {
                    Anchors =
                    [
                        anchor with
                        {
                            ArtifactSha256 = new string('0', 64),
                        },
                        .. first.Anchors[1..],
                    ],
                },
                .. sourceLock.Items[1..],
            ],
        };

        Assert.Throws<InvalidDataException>(missingRights.Validate);
        Assert.Throws<InvalidDataException>(anchorDrift.Validate);
    }

    [Fact]
    public async Task SourceLockRejectsFolderAndReceiptPathTraversal()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        ShowcaseItem first = sourceLock.Items[0];
        ShowcaseSourceLock folderTraversal = sourceLock with
        {
            Items =
            [
                first with
                {
                    DemoGenerated = first.DemoGenerated with
                    {
                        FolderName = $"..{Path.DirectorySeparatorChar}escape",
                    },
                },
                .. sourceLock.Items[1..],
            ],
        };
        ShowcaseArtifact firstArtifact = first.Artifacts[0];
        ShowcaseSourceLock receiptTraversal = sourceLock with
        {
            Items =
            [
                first with
                {
                    Artifacts =
                    [
                        firstArtifact with { Id = "../../victim" },
                        .. first.Artifacts[1..],
                    ],
                },
                .. sourceLock.Items[1..],
            ],
        };

        Assert.Throws<InvalidDataException>(folderTraversal.Validate);
        Assert.Throws<InvalidDataException>(receiptTraversal.Validate);
        Assert.Throws<InvalidDataException>(
            () => ShowcaseInstaller.CombineContainedPath(
                _root,
                "..",
                "victim.json"));
    }

    [Fact]
    public async Task RightsEvidenceHashBindsEveryEvidenceBearingField()
    {
        ShowcaseRights rights = (await LoadLockAsync()).Items.Single(
            static item => item.Id == "sherlock-holmes-1661").Rights;
        string baseline = ShowcaseSourceLock.ComputeRightsEvidenceHash(rights);
        ShowcaseRights[] mutations =
        [
            rights with
            {
                AuthoritativeMetadataUrl =
                    rights.AuthoritativeMetadataUrl + "?changed=1",
            },
            rights with
            {
                AuthoritativeRightsUrl =
                    rights.AuthoritativeRightsUrl + "?changed=1",
            },
            rights with { MirrorEvidenceUrl = "https://example.com/evidence" },
            rights with { PrimaryLicense = rights.PrimaryLicense + "-changed" },
            rights with { LicenseScope = "changed" },
            rights with { Attribution = rights.Attribution + " changed" },
            rights with
            {
                EvidenceRetrievedUtc =
                    rights.EvidenceRetrievedUtc.AddSeconds(1),
            },
            rights with { EvidenceQuote = rights.EvidenceQuote + " changed" },
            rights with { ReviewerState = "changed" },
            rights with { EvidenceNote = "changed" },
            rights with { LocalProcessingBoundary = "changed" },
            rights with
            {
                Exclusions =
                    [rights.Exclusions[0] + " changed", .. rights.Exclusions[1..]],
            },
            rights with { Exclusions = [.. rights.Exclusions.Reverse()] },
            rights with
            {
                MirrorIsRightsAuthority = !rights.MirrorIsRightsAuthority,
            },
            rights with
            {
                EmbeddedEvidence = rights.EmbeddedEvidence! with
                {
                    ArtifactId = "changed",
                },
            },
            rights with
            {
                EmbeddedEvidence = rights.EmbeddedEvidence! with
                {
                    ArtifactSha256 = new string('0', 64),
                },
            },
            rights with
            {
                EmbeddedEvidence = rights.EmbeddedEvidence! with
                {
                    EntryPath = "changed.opf",
                },
            },
            rights with
            {
                EmbeddedEvidence = rights.EmbeddedEvidence! with
                {
                    ExtractorIdentity = "changed",
                },
            },
            rights with
            {
                EmbeddedEvidence = rights.EmbeddedEvidence! with
                {
                    MaximumEntryBytes =
                        rights.EmbeddedEvidence.MaximumEntryBytes + 1,
                },
            },
            rights with
            {
                EmbeddedEvidence = rights.EmbeddedEvidence! with
                {
                    ExpectedTitle = "changed",
                },
            },
            rights with
            {
                EmbeddedEvidence = rights.EmbeddedEvidence! with
                {
                    ExactText = "changed",
                },
            },
        ];

        Assert.All(
            mutations,
            mutation => Assert.NotEqual(
                baseline,
                ShowcaseSourceLock.ComputeRightsEvidenceHash(mutation)));
        Assert.Equal(
            mutations.Length,
            mutations
                .Select(ShowcaseSourceLock.ComputeRightsEvidenceHash)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public async Task SherlockRightsEvidenceIsBoundToExactEpubOpfEntry()
    {
        ShowcaseItem sherlock = (await LoadLockAsync()).Items.Single(
            static item => item.Id == "sherlock-holmes-1661");
        ShowcaseEmbeddedRightsEvidence evidence =
            Assert.IsType<ShowcaseEmbeddedRightsEvidence>(
                sherlock.Rights.EmbeddedEvidence);

        Assert.Equal("Public domain in the USA.", sherlock.Rights.EvidenceQuote);
        Assert.Equal("sherlock-epub", evidence.ArtifactId);
        Assert.Equal(
            "209a8fd827fa88a11f04253d5af10d47967eaa1df3a5c74120ba9aa5e855bba6",
            evidence.ArtifactSha256);
        Assert.Equal("OEBPS/content.opf", evidence.EntryPath);
        Assert.Equal(
            "epub-zip-entry-utf8-xml-dc-rights-v1",
            evidence.ExtractorIdentity);
        Assert.Equal(
            "The Adventures of Sherlock Holmes",
            evidence.ExpectedTitle);
        Assert.Equal("Public domain in the USA.", evidence.ExactText);
        Assert.Contains(
            sherlock.Rights.Exclusions,
            static value => value.Contains(
                "non-U.S.",
                StringComparison.Ordinal));
        Assert.Contains(
            sherlock.Rights.Exclusions,
            static value => value.Contains(
                "registered trademark",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task SintelAnchorsBindExactCueTextAndTimestamps()
    {
        ShowcaseItem sintel = (await LoadLockAsync()).Items.Single(
            static item => item.Id == "sintel-2010");
        ShowcaseAnchor[] anchors = sintel.Anchors;

        Assert.Collection(
            anchors,
            anchor => AssertCue(
                anchor,
                7,
                1_294_000_000,
                1_338_000_000,
                "What brings you to the land of the gatekeepers?"),
            anchor => AssertCue(
                anchor,
                8,
                1_350_000_000,
                1_375_000_000,
                "I'm searching for someone."),
            anchor => AssertCue(
                anchor,
                10,
                1_434_000_000,
                1_450_000_000,
                "A dragon."));
    }

    [Fact]
    public async Task EmbeddedRightsValidatorAcceptsExactTextAndRejectsDrift()
    {
        ShowcaseItem lockedSherlock = (await LoadLockAsync()).Items.Single(
            static item => item.Id == "sherlock-holmes-1661");
        byte[] exactEpub = CreateRightsEpub(
            "The Adventures of Sherlock Holmes",
            "Public domain in the USA.");
        string exactPath = CreateDestination("sherlock.epub");
        await File.WriteAllBytesAsync(exactPath, exactEpub);
        ShowcaseItem exactItem = BindRightsArtifact(
            lockedSherlock,
            exactEpub);

        await ShowcaseInstaller.ValidateEmbeddedRightsEvidenceAsync(
            exactItem,
            exactPath,
            CancellationToken.None);

        byte[] driftedEpub = CreateRightsEpub(
            "A Different Work",
            "Public domain in the USA.");
        string driftedPath = CreateDestination("sherlock-drifted.epub");
        await File.WriteAllBytesAsync(driftedPath, driftedEpub);
        ShowcaseItem driftedItem = BindRightsArtifact(
            lockedSherlock,
            driftedEpub);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ShowcaseInstaller.ValidateEmbeddedRightsEvidenceAsync(
                driftedItem,
                driftedPath,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("archive.org", true)]
    [InlineData("ia801.example.archive.org", true)]
    [InlineData("evilarchive.org", false)]
    [InlineData("archive.org.example.com", false)]
    public void HostAllowlistEnforcesDnsLabelBoundaries(
        string host,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShowcaseInstaller.IsHostAllowed(
                host,
                ["archive.org", ".archive.org"]));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:0a00:0001::1")]
    [InlineData("fd00::1")]
    public void PublicAddressPolicyRejectsPrivateAndMetadataAddresses(
        string address)
    {
        Assert.False(ShowcaseInstaller.IsPublicAddress(
            IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressPolicyAllowsGlobalUnicast(string address)
    {
        Assert.True(ShowcaseInstaller.IsPublicAddress(
            IPAddress.Parse(address)));
    }

    [Fact]
    public async Task ValidateUriRejectsPrivateDnsResolution()
    {
        using HttpClient client = new(new StaticHandler(
            new HttpResponseMessage(HttpStatusCode.OK)));
        ShowcaseInstaller installer = new(
            client,
            dnsResolver: static (_, _) =>
                ValueTask.FromResult(
                    new[] { IPAddress.Parse("169.254.169.254") }));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.ValidateUriAsync(
                new Uri("https://archive.org/file"),
                ["archive.org"],
                CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task CopyAndHashEnforcesExactConfiguredByteCap(
        int sourceLength,
        bool succeeds)
    {
        byte[] bytes = Enumerable.Range(0, sourceLength)
            .Select(static value => (byte)value)
            .ToArray();
        await using MemoryStream source = new(bytes);
        await using MemoryStream destination = new();
        if (succeeds)
        {
            string hash = await ShowcaseInstaller.CopyAndHashAsync(
                source,
                destination,
                expectedSize: 4,
                CancellationToken.None);
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                hash);
            Assert.Equal(bytes, destination.ToArray());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => ShowcaseInstaller.CopyAndHashAsync(
                    source,
                    destination,
                    expectedSize: 4,
                    CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public async Task DownloadPublishesOnlyExactLengthHashAndContentType()
    {
        byte[] bytes = "test"u8.ToArray();
        ShowcaseArtifact artifact = CreateArtifact(bytes);
        using HttpClient client = new(new StaticHandler(
            CreateResponse(bytes)));
        ShowcaseInstaller installer = CreateInstaller(client);
        string destination = CreateDestination();
        ShowcaseTransportObservation observation =
            await installer.DownloadAsync(
            artifact,
            destination,
            CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(
            "dotnet-http-pinned-dns",
            observation.Transport);
        Assert.Equal(
            "https://example.com/test.bin",
            observation.FinalEffectiveUrl);
        Assert.Equal(0, observation.FinalTransferRedirectCount);
        Assert.Equal(
            ["example.com"],
            observation.ObservedManagedRequestHosts);
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(destination)!,
            "*.tmp"));
    }

    [Fact]
    public async Task DownloadLengthMismatchOrInterruptedBodyLeavesNoPublication()
    {
        byte[] bytes = "test"u8.ToArray();
        ShowcaseArtifact artifact = CreateArtifact(bytes);
        HttpResponseMessage wrongLength = CreateResponse(bytes);
        wrongLength.Content.Headers.ContentLength = bytes.Length + 1;
        using HttpClient lengthClient = new(new StaticHandler(wrongLength));
        string lengthDestination = CreateDestination("length.bin");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateInstaller(lengthClient).DownloadAsync(
                artifact,
                lengthDestination,
                CancellationToken.None));
        Assert.False(File.Exists(lengthDestination));

        using HttpClient interruptedClient = new(new StaticHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new InterruptedContent(),
            }));
        string interruptedDestination = CreateDestination("interrupted.bin");
        await Assert.ThrowsAsync<IOException>(
            () => CreateInstaller(interruptedClient).DownloadAsync(
                artifact,
                interruptedDestination,
                CancellationToken.None));
        Assert.False(File.Exists(interruptedDestination));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task ExactCacheValidationReusesOnlyMatchingBytes()
    {
        byte[] bytes = "test"u8.ToArray();
        ShowcaseArtifact artifact = CreateArtifact(bytes);
        string destination = CreateDestination();
        await File.WriteAllBytesAsync(destination, bytes);

        Assert.True(await ShowcaseInstaller.IsValidAsync(
            destination,
            artifact,
            CancellationToken.None));
        bytes[0] ^= 0xFF;
        await File.WriteAllBytesAsync(destination, bytes);
        Assert.False(await ShowcaseInstaller.IsValidAsync(
            destination,
            artifact,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unsupported")]
    public async Task MalformedReceiptIsRejectedBeforeCacheReuse(
        string? transport)
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        ShowcaseArtifact artifact = sourceLock.Items[0].Artifacts[0];
        Directory.CreateDirectory(_root);
        string receiptPath = Path.Combine(_root, "receipt.json");
        await File.WriteAllTextAsync(
            receiptPath,
            JsonSerializer.Serialize(new
            {
                DownloaderIdentity = sourceLock.DownloaderIdentity,
                ArtifactId = artifact.Id,
                Size = artifact.Size,
                Sha256 = artifact.Sha256,
                UserAgent = ShowcaseInstaller.UserAgent,
                AcceptEncoding = "identity",
                TransportObservation = new
                {
                    Transport = transport,
                    FinalEffectiveUrl = artifact.Url,
                    FinalTransferRedirectCount = 0,
                    ObservedManagedRequestHosts = (string[]?)null,
                },
            }));

        ShowcaseAcquisitionReceipt? receipt =
            await ShowcaseInstaller.LoadReceiptAsync(
                receiptPath,
                artifact,
                CancellationToken.None);

        Assert.Null(receipt);
    }

    [Fact]
    public async Task GeneratedMetadataIsDeterministicAndContainsNoRemoteArtwork()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        ShowcaseItem movie = sourceLock.Items.Single(
            static item => item.Id == "sintel-2010");
        ShowcaseItem book = sourceLock.Items.Single(
            static item => item.Id == "ai-for-teachers-2024");
        ShowcaseArtifact bookArtifact = book.Artifacts.Single();

        byte[] firstMovie = ShowcaseInstaller.CreateMovieNfo(movie);
        byte[] secondMovie = ShowcaseInstaller.CreateMovieNfo(movie);
        byte[] firstBook =
            ShowcaseInstaller.CreateBookOpf(book, bookArtifact);
        byte[] secondBook =
            ShowcaseInstaller.CreateBookOpf(book, bookArtifact);

        Assert.Equal(firstMovie, secondMovie);
        Assert.Equal(firstBook, secondBook);
        string combined =
            Encoding.UTF8.GetString(firstMovie) +
            Encoding.UTF8.GetString(firstBook);
        Assert.Contains(movie.Title, combined, StringComparison.Ordinal);
        Assert.Contains(book.Title, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "blender.org",
            combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "pressbooks.pub",
            combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("logo", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thumb", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sintel-2010")]
    [InlineData("spring-2019")]
    [InlineData("coffee-run-2020")]
    [InlineData("ai-for-teachers-2024")]
    [InlineData("digital-humanism-2024")]
    [InlineData("sherlock-holmes-1661")]
    public async Task GeneratedMetadataHashesMatchLock(string itemId)
    {
        ShowcaseItem item = (await LoadLockAsync()).Items.Single(
            item => item.Id == itemId);
        byte[] metadata = item.JellyfinType == "Movie"
            ? ShowcaseInstaller.CreateMovieNfo(item)
            : ShowcaseInstaller.CreateBookOpf(
                item,
                item.Artifacts.Single(static artifact =>
                    artifact.Role == "book"));
        string projected = ShowcaseInstaller.CreateProjectedMetadata(item);
        byte[] sidecar =
            ShowcaseInstaller.CreateMetadataSidecarBytes(item);

        Assert.Equal(
            item.DemoGenerated.MetadataFileSha256,
            Convert.ToHexStringLower(SHA256.HashData(metadata)));
        Assert.Equal(
            item.DemoGenerated.ProjectedMetadataSha256,
            Jellyfin.SemanticDiscovery.Core.Identity.ContentHash.Compute(
                projected));
        Assert.Equal(
            item.DemoGenerated.SemanticSidecarSha256,
            Convert.ToHexStringLower(SHA256.HashData(sidecar)));
    }

    [Fact]
    public void ShowcaseCacheIsExcludedFromGitAndDockerContexts()
    {
        string repository = FindRepositoryRoot();
        string gitIgnore = File.ReadAllText(
            Path.Combine(repository, ".gitignore"));
        string dockerIgnore = File.ReadAllText(
            Path.Combine(repository, ".dockerignore"));

        Assert.Contains(".data/", gitIgnore, StringComparison.Ordinal);
        Assert.Contains(".testagent/", gitIgnore, StringComparison.Ordinal);
        Assert.StartsWith("*", dockerIgnore, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "!.data",
            dockerIgnore,
            StringComparison.Ordinal);
        string showcaseAssets = Path.Combine(
            repository,
            "assets",
            "showcase");
        Assert.All(
            Directory.EnumerateFiles(
                showcaseAssets,
                "*",
                SearchOption.AllDirectories),
            path =>
            {
                Assert.Contains(
                    Path.GetExtension(path),
                    AllowedCommittedExtensions);
                Assert.True(new FileInfo(path).Length < 1024 * 1024);
            });
        string ci = File.ReadAllText(
            Path.Combine(
                repository,
                ".github",
                "workflows",
                "ci.yml"));
        Assert.DoesNotContain(
            "Test-Showcase.ps1",
            ci,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SEMANTIC_DISCOVERY_PROFILE",
            ci,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImplementationDigestIgnoresTransientUntrackedFiles()
    {
        string repository = FindRepositoryRoot();
        string transientPath = Path.Combine(
            repository,
            $"provenance-untracked-{Guid.NewGuid():N}.tmp");
        string before = ShowcaseEvidenceManifestWriter
            .ComputeImplementationContentSha256(repository);
        try
        {
            File.WriteAllText(transientPath, "local transient state");

            Assert.Equal(
                before,
                ShowcaseEvidenceManifestWriter
                    .ComputeImplementationContentSha256(repository));
        }
        finally
        {
            File.Delete(transientPath);
        }
    }

    [Fact]
    public void CommittedMeasuredEvidenceMatchesSanitizerManifest()
    {
        string evidenceRoot = Path.Combine(
            FindRepositoryRoot(),
            "assets",
            "showcase",
            "evidence");
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(evidenceRoot, "evidence-manifest.json")));
        Assert.Equal(
            3,
            manifest.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(
            "43f393b6b419eb95afb0a485756649d4a280a372",
            manifest.RootElement.GetProperty("BaseCommit").GetString());
        Assert.Equal(
            ShowcaseEvidenceManifestWriter.ComputeImplementationContentSha256(
                FindRepositoryRoot()),
            manifest.RootElement.GetProperty(
                "ImplementationContentSha256").GetString());
        JsonElement sanitizer =
            manifest.RootElement.GetProperty("SanitizerPolicy");
        Assert.False(sanitizer.GetProperty("SourceBinariesIncluded").GetBoolean());
        Assert.False(
            sanitizer.GetProperty("BulkExtractedBookTextIncluded").GetBoolean());
        Assert.False(sanitizer.GetProperty("VectorValuesIncluded").GetBoolean());
        JsonElement[] entries = manifest.RootElement
            .GetProperty("Artifacts")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            Directory.EnumerateFiles(evidenceRoot, "*.json")
                .Select(Path.GetFileName)
                .Where(static path =>
                    path != "evidence-manifest.json")
                .Order(StringComparer.Ordinal),
            entries
                .Select(static entry =>
                    entry.GetProperty("Path").GetString())
                .Order(StringComparer.Ordinal));
        foreach (JsonElement entry in entries)
        {
            string path = Path.Combine(
                evidenceRoot,
                entry.GetProperty("Path").GetString()!);
            byte[] content = File.ReadAllBytes(path);
            Assert.Equal(
                entry.GetProperty("Bytes").GetInt64(),
                content.LongLength);
            Assert.Equal(
                entry.GetProperty("Sha256").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(content)));
            string text = Encoding.UTF8.GetString(content);
            Assert.DoesNotContain("data:", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"Text\":", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Vector\":", text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "C:\\",
                text,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "ObservedManagedRequestCount",
                text,
                StringComparison.Ordinal);
            ShowcaseEvidenceManifestWriter.ValidateClassificationSchema(
                entry.GetProperty("Path").GetString()!,
                content,
                entry.GetProperty("Deterministic").GetBoolean());
        }
        Assert.True(entries.Single(static entry =>
            entry.GetProperty("Path").GetString() ==
                "measured-retrieval.json")
            .GetProperty("Deterministic").GetBoolean());
        Assert.True(entries.Single(static entry =>
            entry.GetProperty("Path").GetString() ==
                "measured-acquisition-restart.json")
            .GetProperty("Deterministic").GetBoolean());
        Assert.False(entries.Single(static entry =>
            entry.GetProperty("Path").GetString() ==
                "measured-acquisition-cold.json")
            .GetProperty("Deterministic").GetBoolean());
    }

    [Fact]
    public void EvidenceSchemaValidatorRejectsIncompleteDeterministicFragments()
    {
        byte[] incompleteRetrieval =
            """{"SchemaVersion":3}"""u8.ToArray();
        byte[] warmWithTransport =
            """
            {
              "SchemaVersion": 3,
              "GeneratorId": "showcase-acquisition-evidence-v3",
              "EvidenceKind": "restart-reuse",
              "ProfileId": "showcase-v1",
              "Phase": "warm-restart",
              "DownloaderIdentity": "showcase-downloader-v3-observed-transport-provenance",
              "SourceLockSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "SelectedDownloadBytes": 432489421,
              "DownloadedArtifactCount": 0,
              "ReusedArtifactCount": 7,
              "Artifacts": [],
              "GeneratedFiles": [],
              "NetworkObservations": []
            }
            """u8.ToArray();

        Assert.Throws<InvalidDataException>(
            () => ShowcaseEvidenceManifestWriter
                .ValidateClassificationSchema(
                    "measured-retrieval.json",
                    incompleteRetrieval,
                    deterministic: true));
        Assert.Throws<InvalidDataException>(
            () => ShowcaseEvidenceManifestWriter
                .ValidateClassificationSchema(
                    "measured-acquisition-restart.json",
                    warmWithTransport,
                    deterministic: true));
    }

    [Fact]
    public async Task CommittedAcquisitionEvidenceMatchesLockAndRegeneration()
    {
        string evidenceRoot = Path.Combine(
            FindRepositoryRoot(),
            "assets",
            "showcase",
            "evidence");
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        Dictionary<string, string> expectedGenerated =
            ShowcaseAcquisitionEvidenceWriter.CreateExpectedGeneratedFiles(
                sourceLock);
        ShowcaseAcquisitionEvidence cold =
            JsonSerializer.Deserialize<ShowcaseAcquisitionEvidence>(
                File.ReadAllBytes(Path.Combine(
                    evidenceRoot,
                    "measured-acquisition-cold.json")))
            ?? throw new InvalidDataException("Cold evidence is empty.");
        ShowcaseAcquisitionEvidence restart =
            JsonSerializer.Deserialize<ShowcaseAcquisitionEvidence>(
                File.ReadAllBytes(Path.Combine(
                    evidenceRoot,
                    "measured-acquisition-restart.json")))
            ?? throw new InvalidDataException("Restart evidence is empty.");
        string expectedSourceLockHash =
            ShowcaseEvidenceManifestWriter.ComputeCanonicalTextFileSha256(
                Path.Combine(
                    FindRepositoryRoot(),
                    "assets",
                    "showcase",
                    "sources.lock.json"));

        Assert.Equal(7, cold.DownloadedArtifactCount);
        Assert.Equal(0, cold.ReusedArtifactCount);
        Assert.Equal(0, restart.DownloadedArtifactCount);
        Assert.Equal(7, restart.ReusedArtifactCount);
        Assert.Equal(432_489_421, cold.SelectedDownloadBytes);
        Assert.Equal(expectedSourceLockHash, cold.SourceLockSha256);
        Assert.Equal(expectedSourceLockHash, restart.SourceLockSha256);
        Assert.All(cold.Artifacts, static artifact =>
        {
            Assert.False(artifact.Reused);
            Assert.NotEmpty(artifact.PermittedHosts);
            Assert.NotEmpty(artifact.PermittedTransports);
        });
        Assert.All(restart.Artifacts, static artifact =>
            Assert.True(artifact.Reused));
        Assert.Null(restart.NetworkObservations);
        Assert.NotNull(cold.NetworkObservations);
        Assert.Equal(7, cold.NetworkObservations!.Count);
        Assert.Equal(
            expectedGenerated.OrderBy(static pair => pair.Key),
            cold.GeneratedFiles
                .ToDictionary(
                    static file => file.RelativePath,
                    static file => file.Sha256,
                    StringComparer.Ordinal)
                .OrderBy(static pair => pair.Key));
        Assert.Equal(
            cold.GeneratedFiles,
            restart.GeneratedFiles);
        Assert.Equal(
            sourceLock.Items
                .SelectMany(static item => item.Artifacts)
                .OrderBy(static artifact => artifact.Id)
                .Select(static artifact => (
                    artifact.Id,
                    artifact.Size,
                    artifact.Sha256,
                    artifact.UpstreamSha1)),
            cold.Artifacts
                .OrderBy(static artifact => artifact.Id)
                .Select(static artifact => (
                    artifact.Id,
                    artifact.Size,
                    artifact.Sha256,
                    artifact.UpstreamSha1)));
        string coldText = File.ReadAllText(Path.Combine(
            evidenceRoot,
            "measured-acquisition-cold.json"));
        Assert.DoesNotContain(
            "dn720709",
            coldText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrdinaryAcquisitionEvidenceAllowsAValidatedMixedOutcome()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        ShowcaseArtifactResult[] artifacts = sourceLock.Items
            .SelectMany(static item => item.Artifacts)
            .Select((artifact, index) => new ShowcaseArtifactResult(
                artifact.Id,
                $"library/{artifact.FileName}",
                artifact.Size,
                artifact.Sha256,
                artifact.UpstreamSha1,
                Reused: index != 0,
                TransportObservation: new ShowcaseTransportObservation(
                    artifact.AllowedTransports[0],
                    artifact.Url,
                    FinalTransferRedirectCount: 0,
                    ObservedManagedRequestHosts:
                        [artifact.AllowedRedirectHosts[0]]),
                AllowedRedirectHosts: artifact.AllowedRedirectHosts))
            .ToArray();
        ShowcaseInstallResult actual = new(
            sourceLock.ProfileId,
            sourceLock.DownloaderIdentity,
            sourceLock.SelectedDownloadBytes,
            artifacts,
            ShowcaseAcquisitionEvidenceWriter
                .CreateExpectedGeneratedFiles(sourceLock)
                .Select(static pair =>
                    new ShowcaseGeneratedFile(pair.Key, pair.Value))
                .ToArray(),
            artifacts.SelectMany(static artifact =>
                    artifact.TransportObservation
                        .ObservedManagedRequestHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        string actualPath = Path.Combine(_root, "actual.json");
        string outputPath = Path.Combine(_root, "evidence.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(
            actualPath,
            JsonSerializer.SerializeToUtf8Bytes(actual));

        ShowcaseAcquisitionEvidence evidence =
            await ShowcaseAcquisitionEvidenceWriter.WriteAsync(
                Path.Combine(
                    FindRepositoryRoot(),
                    "assets",
                    "showcase",
                    "sources.lock.json"),
                actualPath,
                "ordinary-initial",
                outputPath,
                CancellationToken.None);

        Assert.Equal(1, evidence.DownloadedArtifactCount);
        Assert.Equal(6, evidence.ReusedArtifactCount);
    }

    [Fact]
    public async Task DirectSpringerCurlSuccessDoesNotClaimIdpObservation()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        ShowcaseArtifactResult[] artifacts = sourceLock.Items
            .SelectMany(static item => item.Artifacts)
            .Select(artifact =>
            {
                ShowcaseTransportObservation observation =
                    artifact.Id == "digital-humanism-pdf"
                        ? new ShowcaseTransportObservation(
                            "curl-pinned-springer",
                            artifact.Url,
                            FinalTransferRedirectCount: 0,
                            ObservedManagedRequestHosts:
                                ["link.springer.com"])
                        : new ShowcaseTransportObservation(
                            "dotnet-http-pinned-dns",
                            artifact.Url,
                            FinalTransferRedirectCount: 0,
                            ObservedManagedRequestHosts:
                                [new Uri(artifact.Url).IdnHost]);
                return new ShowcaseArtifactResult(
                    artifact.Id,
                    $"library/{artifact.FileName}",
                    artifact.Size,
                    artifact.Sha256,
                    artifact.UpstreamSha1,
                    Reused: false,
                    observation,
                    artifact.AllowedRedirectHosts);
            })
            .ToArray();
        ShowcaseInstallResult actual = new(
            sourceLock.ProfileId,
            sourceLock.DownloaderIdentity,
            sourceLock.SelectedDownloadBytes,
            artifacts,
            ShowcaseAcquisitionEvidenceWriter
                .CreateExpectedGeneratedFiles(sourceLock)
                .Select(static pair =>
                    new ShowcaseGeneratedFile(pair.Key, pair.Value))
                .ToArray(),
            artifacts.SelectMany(static artifact =>
                    artifact.TransportObservation
                        .ObservedManagedRequestHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        Directory.CreateDirectory(_root);
        string actualPath = Path.Combine(_root, "direct-springer.json");
        string evidencePath = Path.Combine(_root, "direct-springer-evidence.json");
        await File.WriteAllBytesAsync(
            actualPath,
            JsonSerializer.SerializeToUtf8Bytes(actual));

        ShowcaseAcquisitionEvidence evidence =
            await ShowcaseAcquisitionEvidenceWriter.WriteAsync(
                Path.Combine(
                    FindRepositoryRoot(),
                    "assets",
                    "showcase",
                    "sources.lock.json"),
                actualPath,
                "cold-initial",
                evidencePath,
                CancellationToken.None);

        ShowcaseObservedTransportEvidence springer = evidence
            .NetworkObservations!.Single(static observation =>
                observation.Id == "digital-humanism-pdf");
        Assert.Equal("link.springer.com", springer.FinalEffectiveHostRule);
        Assert.Equal(0, springer.FinalTransferRedirectCount);
        Assert.Equal(1, springer.ObservedManagedHostCount);
        Assert.Equal(
            ["link.springer.com"],
            springer.ObservedManagedHostRules);
        Assert.DoesNotContain(
            "idp.springer.com",
            springer.ObservedManagedHostRules,
            StringComparer.Ordinal);
        Assert.Contains(
            "idp.springer.com",
            evidence.Artifacts.Single(static artifact =>
                artifact.Id == "digital-humanism-pdf").PermittedHosts);
        using JsonDocument serialized = JsonDocument.Parse(
            File.ReadAllBytes(evidencePath));
        JsonElement serializedSpringer = serialized.RootElement
            .GetProperty("NetworkObservations")
            .EnumerateArray()
            .Single(static observation =>
                observation.GetProperty("Id").GetString() ==
                    "digital-humanism-pdf");
        Assert.Equal(
            1,
            serializedSpringer.GetProperty(
                "ObservedManagedHostCount").GetInt32());
        Assert.False(serializedSpringer.TryGetProperty(
            "ObservedManagedRequestCount",
            out _));
    }

    [Fact]
    public void CommittedRetrievalAndNativeEvidenceProveTopRankAndSixDirections()
    {
        string evidenceRoot = Path.Combine(
            FindRepositoryRoot(),
            "assets",
            "showcase",
            "evidence");
        using JsonDocument retrieval = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(
                evidenceRoot,
                "measured-retrieval.json")));
        JsonElement root = retrieval.RootElement;
        float epsilon = root.GetProperty("CosineEpsilon").GetSingle();
        JsonElement[] queries = root.GetProperty("Queries")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(6, queries.Length);
        Assert.All(queries, query =>
        {
            Assert.Equal(1, query.GetProperty("IntendedRank").GetInt32());
            Assert.True(
                query.GetProperty(
                    "SignedMarginOverHighestOther").GetSingle() > epsilon);
        });
        JsonElement[] recommendations = root.GetProperty("Recommendations")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(6, recommendations.Length);
        Assert.All(recommendations, recommendation =>
        {
            float top = recommendation.GetProperty(
                "TopCandidateSimilarity").GetSingle();
            float configured = recommendation.GetProperty(
                "ConfiguredFloor").GetSingle();
            Assert.Equal(
                top > configured,
                recommendation.GetProperty(
                    "EligibleAtConfiguredFloor").GetBoolean());
            Assert.False(recommendation.GetProperty(
                "EligibleAtDefaultFloor").GetBoolean());
        });
        Assert.Equal(
            2,
            recommendations.Count(recommendation =>
                !recommendation.GetProperty(
                    "EligibleAtConfiguredFloor").GetBoolean()));

        using JsonDocument nativeSearch = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(
                evidenceRoot,
                "measured-native-search.json")));
        Assert.Equal(
            6,
            nativeSearch.RootElement.GetProperty("queries")
                .GetArrayLength());
        Assert.Equal(
            0,
            nativeSearch.RootElement.GetProperty("negativeControl")
                .GetProperty("resultCount").GetInt32());
        using JsonDocument nativeSimilar = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(
                evidenceRoot,
                "measured-native-similar.json")));
        Assert.Equal(
            6,
            nativeSimilar.RootElement.GetProperty("directions")
                .GetArrayLength());
        Assert.False(nativeSimilar.RootElement.GetProperty(
            "providerAttributionExposedByApi").GetBoolean());
    }

    [Fact]
    public void CommittedMemoryAndNetworkEvidenceDeclareUnitsAndLimitations()
    {
        string evidenceRoot = Path.Combine(
            FindRepositoryRoot(),
            "assets",
            "showcase",
            "evidence");
        using JsonDocument runtime = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(
                evidenceRoot,
                "measured-runtime-initial.json")));
        JsonElement client = runtime.RootElement.GetProperty("clientMemory");
        Assert.Equal("bytes", client.GetProperty("units").GetString());
        Assert.Equal(
            "process-lifetime-high-water",
            client.GetProperty("metricKind").GetString());
        Assert.True(
            client.GetProperty("peakWorkingSetBytes").GetInt64() >=
            client.GetProperty("currentWorkingSetBytes").GetInt64());

        using JsonDocument server = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(
                evidenceRoot,
                "measured-server-memory.json")));
        JsonElement[] observations = server.RootElement
            .GetProperty("observations")
            .EnumerateArray()
            .ToArray();
        Assert.True(observations.Length >= 3);
        Assert.Contains(
            observations,
            observation =>
                observation.GetProperty("decodedSourceBytes").GetInt64() ==
                    25_808_724 &&
                observation.GetProperty("units").GetString() == "kib" &&
                observation.GetProperty("metricKind").GetString() ==
                    "process_lifetime_high_water");

        using JsonDocument network = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(
                evidenceRoot,
                "measured-network-policy.json")));
        JsonElement jellyfin =
            network.RootElement.GetProperty("jellyfin");
        Assert.True(jellyfin.GetProperty(
            "initialExternalHttpCanaryBlocked").GetBoolean());
        Assert.True(jellyfin.GetProperty(
            "restartExternalHttpCanaryBlocked").GetBoolean());
        Assert.Contains(
            "direct socket",
            jellyfin.GetProperty("residualLimitation").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PurgeDeletesOnlyOwnedShowcaseRootAndPreservesSyntheticSibling()
    {
        string data = Path.Combine(_root, "data");
        string showcase = Path.Combine(data, "showcase-profile");
        string synthetic = Path.Combine(data, "synthetic");
        Directory.CreateDirectory(showcase);
        Directory.CreateDirectory(synthetic);
        File.WriteAllText(
            Path.Combine(showcase, "showcase.actual.json"),
            """{"ProfileId":"showcase-v1","DownloaderIdentity":"showcase-downloader-v3-observed-transport-provenance"}""");
        File.WriteAllText(Path.Combine(showcase, "artifact.bin"), "owned");
        string syntheticSentinel = Path.Combine(synthetic, "keep.txt");
        File.WriteAllText(syntheticSentinel, "keep");

        ShowcasePurger.Purge(showcase);

        Assert.False(Directory.Exists(showcase));
        Assert.Equal("keep", File.ReadAllText(syntheticSentinel));
    }

    [Fact]
    public void PurgeRejectsUnownedDirectory()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "unrelated.txt"), "keep");

        Assert.Throws<InvalidDataException>(
            () => ShowcasePurger.Purge(_root));

        Assert.True(File.Exists(Path.Combine(_root, "unrelated.txt")));
    }

    [Fact]
    public void RuntimePurgeRequiresMarkerAndPreservesSyntheticSibling()
    {
        string data = Path.Combine(_root, "data");
        string runtime = Path.Combine(data, "showcase-runtime");
        string synthetic = Path.Combine(data, "synthetic");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(synthetic);
        string syntheticSentinel = Path.Combine(synthetic, "keep.txt");
        File.WriteAllText(syntheticSentinel, "keep");
        Assert.Throws<InvalidDataException>(
            () => ShowcaseRuntimePurger.Purge(runtime));
        File.WriteAllText(
            Path.Combine(
                runtime,
                ShowcaseRuntimePurger.OwnershipMarkerFileName),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                ProfileId = "showcase-v1",
                Purpose = ShowcaseRuntimePurger.OwnershipPurpose,
            }));
        File.WriteAllText(Path.Combine(runtime, "index.json"), "owned");

        ShowcaseRuntimePurger.Purge(runtime);

        Assert.False(Directory.Exists(runtime));
        Assert.Equal("keep", File.ReadAllText(syntheticSentinel));
    }

    [Fact]
    public void RuntimeOwnershipNeverAdoptsNonemptyUnownedDirectory()
    {
        string runtime = Path.Combine(_root, "showcase-runtime");
        Directory.CreateDirectory(runtime);
        string unrelated = Path.Combine(runtime, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");

        Assert.Throws<InvalidDataException>(
            () => ShowcaseRuntimePurger.EnsureOwned(runtime));
        Assert.False(File.Exists(Path.Combine(
            runtime,
            ShowcaseRuntimePurger.OwnershipMarkerFileName)));
        Assert.Equal("keep", File.ReadAllText(unrelated));

        File.Delete(unrelated);
        Assert.Throws<InvalidDataException>(
            () => ShowcaseRuntimePurger.EnsureOwned(runtime));
        Directory.Delete(runtime);
        ShowcaseRuntimePurger.EnsureOwned(runtime);
        string marker = Path.Combine(
            runtime,
            ShowcaseRuntimePurger.OwnershipMarkerFileName);
        byte[] originalMarker = File.ReadAllBytes(marker);
        File.WriteAllText(unrelated, "owned");
        ShowcaseRuntimePurger.EnsureOwned(runtime);

        Assert.Equal(originalMarker, File.ReadAllBytes(marker));
        Assert.Equal("owned", File.ReadAllText(unrelated));
        ShowcaseRuntimePurger.Purge(runtime);
    }

    [Fact]
    public async Task FailedInitialAcquisitionLeavesOwnedRootThatCanBePurged()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        using HttpClient client = new(new StaticHandler(
            CreateResponse("wrong"u8.ToArray())));
        ShowcaseInstaller installer = CreateInstaller(client);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.InstallAsync(
                sourceLock,
                _root,
                CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(
            _root,
            ShowcaseInstaller.OwnershipMarkerFileName)));
        Assert.False(File.Exists(Path.Combine(
            _root,
            "showcase.actual.json")));
        ShowcasePurger.Purge(_root);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AcquisitionRefusesNonemptyUnownedRoot()
    {
        Directory.CreateDirectory(_root);
        string unrelated = Path.Combine(_root, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        using HttpClient client = new(new StaticHandler(
            CreateResponse("wrong"u8.ToArray())));
        ShowcaseSourceLock sourceLock = await LoadLockAsync();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateInstaller(client).InstallAsync(
                sourceLock,
                _root,
                CancellationToken.None));

        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.False(File.Exists(Path.Combine(
            _root,
            ShowcaseInstaller.OwnershipMarkerFileName)));
    }

    [Fact]
    public async Task FailedReacquisitionInvalidatesPriorCompletionManifest()
    {
        ShowcaseSourceLock sourceLock = await LoadLockAsync();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, ShowcaseInstaller.OwnershipMarkerFileName),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                ProfileId = sourceLock.ProfileId,
                DownloaderIdentity = sourceLock.DownloaderIdentity,
            }));
        string completionPath = Path.Combine(
            _root,
            "showcase.actual.json");
        await File.WriteAllTextAsync(
            completionPath,
            JsonSerializer.Serialize(new
            {
                ProfileId = sourceLock.ProfileId,
                DownloaderIdentity = sourceLock.DownloaderIdentity,
            }));
        using HttpClient client = new(new StaticHandler(
            CreateResponse("wrong"u8.ToArray())));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateInstaller(client).InstallAsync(
                sourceLock,
                _root,
                CancellationToken.None));

        Assert.False(File.Exists(completionPath));
        Assert.True(File.Exists(Path.Combine(
            _root,
            ShowcaseInstaller.OwnershipMarkerFileName)));
        ShowcasePurger.Purge(_root);
    }

    private static ShowcaseInstaller CreateInstaller(HttpClient client) =>
        new(
            client,
            dnsResolver: static (_, _) =>
                ValueTask.FromResult(
                    new[] { IPAddress.Parse("8.8.8.8") }));

    private static ShowcaseArtifact CreateArtifact(byte[] bytes) => new()
    {
        Id = "test-artifact",
        Role = "book",
        Url = "https://example.com/test.bin",
        RetrievedUtc = DateTimeOffset.UnixEpoch,
        Size = bytes.Length,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ContentTypes = ["application/octet-stream"],
        FileName = "test.bin",
        AllowedRedirectHosts = ["example.com"],
        AllowedTransports = ["dotnet-http-pinned-dns"],
    };

    private static HttpResponseMessage CreateResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers =
                {
                    ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                        "application/octet-stream"),
                },
            },
        };

    private string CreateDestination(string name = "artifact.bin")
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, name);
    }

    private static ShowcaseItem BindRightsArtifact(
        ShowcaseItem item,
        byte[] epub)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(epub));
        ShowcaseArtifact artifact = item.Artifacts.Single() with
        {
            Size = epub.Length,
            Sha256 = hash,
            FileName = "sherlock.epub",
        };
        return item with
        {
            Artifacts = [artifact],
            Rights = item.Rights with
            {
                EmbeddedEvidence = item.Rights.EmbeddedEvidence! with
                {
                    ArtifactSha256 = hash,
                },
            },
        };
    }

    private static void AssertCue(
        ShowcaseAnchor anchor,
        int index,
        long startTicks,
        long endTicks,
        string text)
    {
        Assert.Equal(index, anchor.CueIndex);
        Assert.Equal(startTicks, anchor.CueStartTicks);
        Assert.Equal(endTicks, anchor.CueEndTicks);
        Assert.Equal(text, anchor.Text);
        Assert.Contains(
            $"cue {index}",
            anchor.Location,
            StringComparison.Ordinal);
    }

    private static byte[] CreateRightsEpub(
        string title,
        string rightsText)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(
                   buffer,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                "OEBPS/content.opf",
                CompressionLevel.SmallestSize);
            using StreamWriter writer = new(
                entry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<package xmlns=\"http://www.idpf.org/2007/opf\" " +
                "xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                $"<metadata><dc:title>{title}</dc:title>" +
                $"<dc:rights>{rightsText}</dc:rights></metadata>" +
                "</package>");
        }

        return buffer.ToArray();
    }

    private static async Task<ShowcaseSourceLock> LoadLockAsync() =>
        await ShowcaseSourceLock.LoadAsync(
            Path.Combine(
                FindRepositoryRoot(),
                "assets",
                "showcase",
                "sources.lock.json"));

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class StaticHandler(HttpResponseMessage response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class InterruptedContent : HttpContent
    {
        public InterruptedContent()
        {
            Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.FromException(new IOException("interrupted"));

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new InterruptedStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 4;
            return true;
        }
    }

    private sealed class InterruptedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 4;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("interrupted");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("interrupted"));
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
