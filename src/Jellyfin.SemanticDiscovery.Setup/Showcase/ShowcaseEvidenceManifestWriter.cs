using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Setup.Showcase;

public static class ShowcaseEvidenceManifestWriter
{
    public const string GeneratorId = "showcase-evidence-manifest-v3";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    private static readonly IReadOnlyDictionary<string, EvidenceClassification>
        Classifications =
            new Dictionary<string, EvidenceClassification>(
                StringComparer.Ordinal)
            {
                ["measured-acquisition-cold.json"] = new(
                    "cold-acquisition",
                    "Seven exact network acquisitions and deterministic generated-file identities.",
                    Deterministic: false),
                ["measured-acquisition-restart.json"] = new(
                    "restart-reuse",
                    "Seven receipt/hash-validated cache reuses with zero downloads.",
                    Deterministic: true),
                ["measured-retrieval.json"] = new(
                    "direct-csharp-in-memory-vector-store",
                    "Direct C# in-memory vector-store matrices, anchors, request sizes, and scoped rankings.",
                    Deterministic: true),
                ["measured-native-search.json"] = new(
                    "native-jellyfin-search",
                    "Sanitized Jellyfin Search/Hints ordering for six positive and one negative query.",
                    Deterministic: false),
                ["measured-native-similar.json"] = new(
                    "native-jellyfin-similar",
                    "Sanitized Jellyfin Similar API outcomes for all six directed sources.",
                    Deterministic: false),
                ["measured-runtime-initial.json"] = new(
                    "showcase-runtime-status",
                    "Authoritative plugin counters, effective floor, client memory, and network diagnostics.",
                    Deterministic: false),
                ["measured-runtime-restart.json"] = new(
                    "showcase-runtime-status",
                    "Restart/rescan counters and stable runtime policy diagnostics.",
                    Deterministic: false),
                ["measured-server-memory.json"] = new(
                    "markitdown-server-memory",
                    "Sanitized process-lifetime high-water/current RSS observations.",
                    Deterministic: false),
                ["measured-network-policy.json"] = new(
                    "runtime-network-controls",
                    "Jellyfin proxy-aware HTTP and MarkItDown Python socket canary outcomes.",
                    Deterministic: false),
            };

    public static async Task<ShowcaseEvidenceManifest> WriteAsync(
        string repositoryRoot,
        string evidenceRoot,
        string baseCommit,
        string outputPath,
        CancellationToken cancellationToken)
    {
        string repository = Path.GetFullPath(repositoryRoot);
        string evidence = Path.GetFullPath(evidenceRoot);
        if (!IsHex(baseCommit, 40))
        {
            throw new ArgumentException(
                "Evidence base commit must be a full 40-character SHA-1.",
                nameof(baseCommit));
        }

        string[] files = Directory.EnumerateFiles(
                evidence,
                "*.json",
                SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(outputPath),
                PathComparison()))
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        string[] names = files.Select(Path.GetFileName).ToArray()!;
        if (!names.SequenceEqual(
                Classifications.Keys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Showcase evidence directory does not contain the exact classified artifact set.");
        }

        List<ShowcaseEvidenceFile> artifacts = [];
        foreach (string path in files)
        {
            byte[] bytes = await File.ReadAllBytesAsync(
                path,
                cancellationToken).ConfigureAwait(false);
            string name = Path.GetFileName(path);
            EvidenceClassification classification = Classifications[name];
            ValidateClassificationSchema(name, bytes, classification);
            artifacts.Add(new ShowcaseEvidenceFile(
                name,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                classification.Kind,
                classification.Proves,
                classification.Deterministic));
        }

        ShowcaseEvidenceManifest manifest = new(
            SchemaVersion: 3,
            GeneratorId,
            ProfileId: "showcase-v1",
            BaseCommit: baseCommit,
            ImplementationContentSha256:
                ComputeImplementationContentSha256(repository),
            SourceLockSha256: ComputeCanonicalTextFileSha256(
                Path.Combine(
                    repository,
                    "assets",
                    "showcase",
                    "sources.lock.json")),
            GeneratorVersions: new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["acquisition"] =
                    ShowcaseAcquisitionEvidenceWriter.GeneratorId,
                ["retrieval"] = ShowcaseEvidenceWriter.GeneratorId,
                ["nativeSearch"] =
                    "showcase-native-search-evidence-v3",
                ["nativeSimilar"] =
                    "showcase-native-similar-evidence-v3",
                ["runtime"] = "showcase-runtime-evidence-v3",
                ["network"] = "showcase-network-evidence-v3",
                ["memory"] =
                    "showcase-server-memory-evidence-v3",
            },
            SanitizerPolicy: new ShowcaseSanitizerPolicy(
                SourceBinariesIncluded: false,
                BulkExtractedBookTextIncluded: false,
                ContentChunksIncluded: false,
                VectorValuesIncluded: false,
                CredentialsIncluded: false,
                CookiesIncluded: false,
                AuthorizationHeadersIncluded: false,
                PayloadBodiesIncluded: false,
                RemoteArtworkIncluded: false,
                BookScreenshotsIncluded: false),
            artifacts);
        await File.WriteAllBytesAsync(
            outputPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    internal static void ValidateClassificationSchema(
        string name,
        ReadOnlyMemory<byte> bytes,
        bool deterministic)
    {
        if (!Classifications.TryGetValue(
                name,
                out EvidenceClassification? classification) ||
            classification.Deterministic != deterministic)
        {
            throw new InvalidDataException(
                "Evidence classification is missing or inconsistent.");
        }

        ValidateClassificationSchema(name, bytes, classification);
    }

    private static void ValidateClassificationSchema(
        string name,
        ReadOnlyMemory<byte> bytes,
        EvidenceClassification classification)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        switch (name)
        {
            case "measured-acquisition-cold.json":
                ValidateAcquisitionEvidence(
                    root,
                    classification,
                    cold: true);
                return;
            case "measured-acquisition-restart.json":
                ValidateAcquisitionEvidence(
                    root,
                    classification,
                    cold: false);
                return;
            case "measured-retrieval.json":
                ValidateRetrievalEvidence(root, classification);
                return;
            case "measured-native-search.json":
                ValidateNativeSearchEvidence(root, classification);
                return;
            case "measured-native-similar.json":
                ValidateNativeSimilarEvidence(root, classification);
                return;
            case "measured-runtime-initial.json":
            case "measured-runtime-restart.json":
                ValidateRuntimeEvidence(root, classification);
                return;
            case "measured-server-memory.json":
                ValidateServerMemoryEvidence(root, classification);
                return;
            case "measured-network-policy.json":
                ValidateNetworkEvidence(root, classification);
                return;
            default:
                throw new InvalidDataException(
                    $"Evidence artifact '{name}' has no schema validator.");
        }
    }

    private static void ValidateAcquisitionEvidence(
        JsonElement root,
        EvidenceClassification classification,
        bool cold)
    {
        string[] properties = cold
            ?
            [
                "SchemaVersion",
                "GeneratorId",
                "EvidenceKind",
                "ProfileId",
                "Phase",
                "DownloaderIdentity",
                "SourceLockSha256",
                "SelectedDownloadBytes",
                "DownloadedArtifactCount",
                "ReusedArtifactCount",
                "Artifacts",
                "GeneratedFiles",
                "NetworkObservations",
            ]
            :
            [
                "SchemaVersion",
                "GeneratorId",
                "EvidenceKind",
                "ProfileId",
                "Phase",
                "DownloaderIdentity",
                "SourceLockSha256",
                "SelectedDownloadBytes",
                "DownloadedArtifactCount",
                "ReusedArtifactCount",
                "Artifacts",
                "GeneratedFiles",
            ];
        RequireExactProperties(root, properties);
        RequireInteger(root, "SchemaVersion", 3);
        RequireString(root, "GeneratorId", ShowcaseAcquisitionEvidenceWriter.GeneratorId);
        RequireString(root, "EvidenceKind", classification.Kind);
        RequireString(root, "ProfileId", "showcase-v1");
        RequireString(
            root,
            "Phase",
            cold ? "cold-initial" : "warm-restart");
        RequireString(
            root,
            "DownloaderIdentity",
            ShowcaseSourceLock.ExpectedDownloaderIdentity);
        RequireSha256(root, "SourceLockSha256");
        RequireInteger(root, "SelectedDownloadBytes", 432_489_421);
        RequireInteger(root, "DownloadedArtifactCount", cold ? 7 : 0);
        RequireInteger(root, "ReusedArtifactCount", cold ? 0 : 7);
        JsonElement artifacts = RequireArray(root, "Artifacts", 7);
        foreach (JsonElement artifact in artifacts.EnumerateArray())
        {
            RequireAllowedProperties(
                artifact,
                required:
                [
                    "Id",
                    "RelativePath",
                    "Size",
                    "Sha256",
                    "Reused",
                    "PermittedTransports",
                    "PermittedHosts",
                ],
                optional: ["UpstreamSha1"]);
            RequireNonEmptyString(artifact, "Id");
            RequireNonEmptyString(artifact, "RelativePath");
            RequirePositiveInteger(artifact, "Size");
            RequireSha256(artifact, "Sha256");
            RequireBoolean(artifact, "Reused", !cold);
            RequireNonEmptyArray(artifact, "PermittedTransports");
            RequireNonEmptyArray(artifact, "PermittedHosts");
        }

        JsonElement generated = RequireArray(root, "GeneratedFiles", 13);
        foreach (JsonElement file in generated.EnumerateArray())
        {
            RequireExactProperties(file, "RelativePath", "Sha256");
            RequireNonEmptyString(file, "RelativePath");
            RequireSha256(file, "Sha256");
        }

        if (cold)
        {
            if (classification.Deterministic)
            {
                throw new InvalidDataException(
                    "Cold acquisition evidence cannot be deterministic.");
            }

            JsonElement observations =
                RequireArray(root, "NetworkObservations", 7);
            foreach (JsonElement observation in observations.EnumerateArray())
            {
                RequireExactProperties(
                    observation,
                    "Id",
                    "Transport",
                    "FinalEffectiveHostRule",
                    "FinalTransferRedirectCount",
                    "ObservedManagedHostCount",
                    "ObservedManagedHostRules");
                RequireNonEmptyString(observation, "Id");
                RequireNonEmptyString(observation, "Transport");
                RequireNonEmptyString(
                    observation,
                    "FinalEffectiveHostRule");
                RequireNonNegativeInteger(
                    observation,
                    "FinalTransferRedirectCount");
                int count = checked((int)RequirePositiveInteger(
                    observation,
                    "ObservedManagedHostCount"));
                RequireArray(
                    observation,
                    "ObservedManagedHostRules",
                    count);
                if (observation.TryGetProperty(
                        "ObservedManagedRequestCount",
                        out _))
                {
                    throw new InvalidDataException(
                        "Cold evidence contains the legacy managed-request count field.");
                }
            }
        }
        else if (!classification.Deterministic ||
                 root.TryGetProperty("NetworkObservations", out _) ||
                 Encoding.UTF8.GetString(
                     JsonSerializer.SerializeToUtf8Bytes(root))
                     .Contains(
                         "TransportObservation",
                         StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Restart acquisition evidence contains run-dependent transport facts.");
        }
    }

    private static void ValidateRetrievalEvidence(
        JsonElement root,
        EvidenceClassification classification)
    {
        RequireExactProperties(
            root,
            "SchemaVersion",
            "GeneratorId",
            "EvidenceKind",
            "AccessControlBoundary",
            "ProjectionHashContract",
            "MeasurementMetadataFloor",
            "MeasurementContentFloor",
            "ProfileId",
            "IndexIdentity",
            "SnapshotContentId",
            "VectorPayloadSha256",
            "CosineEpsilon",
            "MetadataCount",
            "ContentChunkCount",
            "RecommendationFloor",
            "Anchors",
            "Recommendations",
            "Matrices",
            "RequestSizes",
            "Queries",
            "Items");
        if (!classification.Deterministic)
        {
            throw new InvalidDataException(
                "Retrieval evidence must be classified deterministic.");
        }

        RequireInteger(root, "SchemaVersion", 3);
        RequireString(root, "GeneratorId", ShowcaseEvidenceWriter.GeneratorId);
        RequireString(root, "EvidenceKind", classification.Kind);
        RequireNonEmptyString(root, "AccessControlBoundary");
        RequireNonEmptyString(root, "ProjectionHashContract");
        RequireNumber(root, "MeasurementMetadataFloor");
        RequireNumber(root, "MeasurementContentFloor");
        RequireString(root, "ProfileId", "showcase-v1");
        RequireSha256(root, "IndexIdentity");
        RequireSha256(root, "SnapshotContentId");
        RequireSha256(root, "VectorPayloadSha256");
        RequirePositiveNumber(root, "CosineEpsilon");
        RequireInteger(root, "MetadataCount", 6);
        RequirePositiveInteger(root, "ContentChunkCount");
        RequirePositiveNumber(root, "RecommendationFloor");
        ValidateRetrievalAnchors(RequireArray(root, "Anchors", 8));
        ValidateRetrievalRecommendations(
            RequireArray(root, "Recommendations", 6));
        ValidateRetrievalMatrices(RequireArray(root, "Matrices", 2));
        ValidateRetrievalRequestSizes(
            RequireArray(root, "RequestSizes", 3));
        ValidateRetrievalQueries(RequireArray(root, "Queries", 6));
        ValidateRetrievalItems(RequireArray(root, "Items", 6));
        if (root.TryGetProperty("SnapshotSha256", out _))
        {
            throw new InvalidDataException(
                "Retrieval evidence contains a timestamp-dependent snapshot hash.");
        }
    }

    private static void ValidateRetrievalAnchors(JsonElement anchors)
    {
        foreach (JsonElement anchor in anchors.EnumerateArray())
        {
            RequireExactProperties(
                anchor,
                "ItemId",
                "ArtifactSha256",
                "ExtractorIdentity",
                "Normalization",
                "Location",
                "Matched",
                "CueIndex",
                "CueStartTicks",
                "CueEndTicks",
                "ContainingChunkStartTicks",
                "ContainingChunkEndTicks");
            RequireNonEmptyString(anchor, "ItemId");
            RequireSha256(anchor, "ArtifactSha256");
            RequireNonEmptyString(anchor, "ExtractorIdentity");
            RequireNonEmptyString(anchor, "Normalization");
            RequireNonEmptyString(anchor, "Location");
            RequireBoolean(anchor, "Matched", true);
            long? cueIndex = RequireNullableInteger(anchor, "CueIndex");
            long? cueStart = RequireNullableInteger(
                anchor,
                "CueStartTicks");
            long? cueEnd = RequireNullableInteger(anchor, "CueEndTicks");
            long? chunkStart = RequireNullableInteger(
                anchor,
                "ContainingChunkStartTicks");
            long? chunkEnd = RequireNullableInteger(
                anchor,
                "ContainingChunkEndTicks");
            if (cueIndex is null)
            {
                if (cueStart is not null || cueEnd is not null ||
                    chunkStart is not null || chunkEnd is not null)
                {
                    throw new InvalidDataException(
                        "Non-cue retrieval anchors cannot contain timing fields.");
                }
            }
            else if (cueIndex <= 0 || cueStart is null || cueEnd is null ||
                     chunkStart is null || chunkEnd is null ||
                     cueStart < 0 || cueStart >= cueEnd ||
                     chunkStart > cueStart || chunkEnd < cueEnd)
            {
                throw new InvalidDataException(
                    "Cue retrieval anchor timing is invalid.");
            }
        }
    }

    private static void ValidateRetrievalRecommendations(
        JsonElement recommendations)
    {
        foreach (JsonElement recommendation in recommendations.EnumerateArray())
        {
            RequireExactProperties(
                recommendation,
                "SourceItemId",
                "ExpectedItemId",
                "ExpectedRank",
                "TopCandidateSimilarity",
                "DefaultFloor",
                "ConfiguredFloor",
                "EligibleAtDefaultFloor",
                "EligibleAtConfiguredFloor",
                "Ranking");
            RequireNonEmptyString(recommendation, "SourceItemId");
            RequireStringOrNull(recommendation, "ExpectedItemId");
            long? rank = RequireNullableInteger(
                recommendation,
                "ExpectedRank");
            if (rank is not (null or 1))
            {
                throw new InvalidDataException(
                    "Expected recommendation rank must be null or one.");
            }

            RequireNumber(recommendation, "TopCandidateSimilarity");
            RequirePositiveNumber(recommendation, "DefaultFloor");
            RequirePositiveNumber(recommendation, "ConfiguredFloor");
            RequireBoolean(recommendation, "EligibleAtDefaultFloor");
            RequireBoolean(recommendation, "EligibleAtConfiguredFloor");
            JsonElement ranking = RequireArray(
                recommendation,
                "Ranking",
                2);
            foreach (JsonElement candidate in ranking.EnumerateArray())
            {
                RequireExactProperties(candidate, "ItemId", "Similarity");
                RequireNonEmptyString(candidate, "ItemId");
                RequireNumber(candidate, "Similarity");
            }
        }
    }

    private static void ValidateRetrievalMatrices(JsonElement matrices)
    {
        foreach (JsonElement matrix in matrices.EnumerateArray())
        {
            RequireExactProperties(
                matrix,
                "ItemType",
                "ItemIds",
                "Cosine",
                "CandidateOrder");
            RequireNonEmptyString(matrix, "ItemType");
            string[] itemIds = ValidateStringArray(
                RequireArray(matrix, "ItemIds", 3),
                unique: true);
            JsonElement cosine = RequireArray(matrix, "Cosine", 3);
            foreach (JsonElement row in cosine.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array ||
                    row.GetArrayLength() != 3 ||
                    row.EnumerateArray().Any(static value =>
                        value.ValueKind != JsonValueKind.Number ||
                        !value.TryGetDouble(out double number) ||
                        !double.IsFinite(number)))
                {
                    throw new InvalidDataException(
                        "Retrieval cosine matrix must be finite and 3x3.");
                }
            }

            JsonElement order = RequireObject(matrix, "CandidateOrder");
            string[] keys = order.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            if (!keys.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(itemIds))
            {
                throw new InvalidDataException(
                    "Retrieval candidate order keys must match matrix item IDs.");
            }

            foreach (JsonProperty property in order.EnumerateObject())
            {
                ValidateStringArray(
                    property.Value,
                    unique: true,
                    expectedCount: 2);
            }
        }
    }

    private static void ValidateRetrievalRequestSizes(
        JsonElement requestSizes)
    {
        foreach (JsonElement request in requestSizes.EnumerateArray())
        {
            RequireExactProperties(
                request,
                "ArtifactId",
                "SourceBytes",
                "SerializedRequestBytes",
                "HardRequestCapBytes");
            RequireNonEmptyString(request, "ArtifactId");
            RequirePositiveInteger(request, "SourceBytes");
            RequirePositiveInteger(request, "SerializedRequestBytes");
            RequirePositiveInteger(request, "HardRequestCapBytes");
        }
    }

    private static void ValidateRetrievalQueries(JsonElement queries)
    {
        foreach (JsonElement query in queries.EnumerateArray())
        {
            RequireExactProperties(
                query,
                "IntendedItemId",
                "Query",
                "IntendedRank",
                "IntendedScore",
                "HighestOtherScore",
                "SignedMarginOverHighestOther",
                "Results");
            RequireNonEmptyString(query, "IntendedItemId");
            RequireNonEmptyString(query, "Query");
            RequireInteger(query, "IntendedRank", 1);
            RequireNumber(query, "IntendedScore");
            RequireNumber(query, "HighestOtherScore");
            RequirePositiveNumber(
                query,
                "SignedMarginOverHighestOther");
            JsonElement results = RequireArray(query, "Results", 3);
            foreach (JsonElement result in results.EnumerateArray())
            {
                RequireExactProperties(
                    result,
                    "ItemId",
                    "ItemType",
                    "Score");
                RequireNonEmptyString(result, "ItemId");
                RequireNonEmptyString(result, "ItemType");
                RequireNumber(result, "Score");
            }
        }
    }

    private static void ValidateRetrievalItems(JsonElement items)
    {
        foreach (JsonElement item in items.EnumerateArray())
        {
            RequireExactProperties(
                item,
                "LockItemId",
                "JellyfinItemId",
                "CanonicalProjectionSha256");
            RequireNonEmptyString(item, "LockItemId");
            if (!Guid.TryParse(
                    RequireNonEmptyString(item, "JellyfinItemId"),
                    out _))
            {
                throw new InvalidDataException(
                    "Retrieval Jellyfin item ID must be a GUID.");
            }

            RequireSha256(item, "CanonicalProjectionSha256");
        }
    }

    private static void ValidateNativeSearchEvidence(
        JsonElement root,
        EvidenceClassification classification)
    {
        RequireExactProperties(
            root,
            "schemaVersion",
            "generatorId",
            "evidenceKind",
            "profileId",
            "source",
            "accessControlBoundary",
            "queries",
            "negativeControl");
        RequireInteger(root, "schemaVersion", 2);
        RequireString(
            root,
            "generatorId",
            "showcase-native-search-evidence-v3");
        RequireString(root, "evidenceKind", classification.Kind);
        RequireString(root, "profileId", "showcase-v1");
        RequireNonEmptyString(root, "source");
        RequireNonEmptyString(root, "accessControlBoundary");
        JsonElement queries = RequireArray(root, "queries", 6);
        foreach (JsonElement query in queries.EnumerateArray())
        {
            RequireExactProperties(
                query,
                "query",
                "intendedTitle",
                "intendedType",
                "results");
            RequireNonEmptyString(query, "query");
            RequireNonEmptyString(query, "intendedTitle");
            RequireNonEmptyString(query, "intendedType");
            ValidateNativeItems(
                RequireNonEmptyArray(query, "results"));
        }

        JsonElement negative = RequireObject(root, "negativeControl");
        RequireExactProperties(negative, "query", "resultCount");
        RequireNonEmptyString(negative, "query");
        RequireInteger(negative, "resultCount", 0);
    }

    private static void ValidateNativeSimilarEvidence(
        JsonElement root,
        EvidenceClassification classification)
    {
        RequireExactProperties(
            root,
            "schemaVersion",
            "generatorId",
            "evidenceKind",
            "profileId",
            "source",
            "strictFloorComparison",
            "providerAttributionExposedByApi",
            "knownJellyfinBoundary",
            "directions");
        RequireInteger(root, "schemaVersion", 2);
        RequireString(
            root,
            "generatorId",
            "showcase-native-similar-evidence-v3");
        RequireString(root, "evidenceKind", classification.Kind);
        RequireString(root, "profileId", "showcase-v1");
        RequireNonEmptyString(root, "source");
        RequireString(root, "strictFloorComparison", ">");
        RequireBoolean(
            root,
            "providerAttributionExposedByApi",
            false);
        RequireNonEmptyString(root, "knownJellyfinBoundary");
        JsonElement directions = RequireArray(root, "directions", 6);
        foreach (JsonElement direction in directions.EnumerateArray())
        {
            RequireExactProperties(
                direction,
                "sourceTitle",
                "sourceId",
                "semanticExpectedTitle",
                "nativeExpectedTitle",
                "providerBoundary",
                "semanticRuntimeEffectiveFloor",
                "semanticRuntimeResults",
                "results");
            RequireNonEmptyString(direction, "sourceTitle");
            RequireNonEmptyString(direction, "sourceId");
            RequireStringOrNull(direction, "semanticExpectedTitle");
            RequireStringOrNull(direction, "nativeExpectedTitle");
            RequireNonEmptyString(direction, "providerBoundary");
            RequirePositiveNumber(
                direction,
                "semanticRuntimeEffectiveFloor");
            JsonElement semantic = RequireProperty(
                direction,
                "semanticRuntimeResults");
            if (semantic.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Semantic runtime results must be an array.");
            }

            foreach (JsonElement result in semantic.EnumerateArray())
            {
                RequireExactProperties(result, "itemId", "score");
                RequireNonEmptyString(result, "itemId");
                RequireNumber(result, "score");
            }

            JsonElement native = RequireProperty(direction, "results");
            if (native.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Native similar results must be an array.");
            }

            ValidateNativeItems(native);
        }
    }

    private static void ValidateRuntimeEvidence(
        JsonElement root,
        EvidenceClassification classification)
    {
        RequireExactProperties(
            root,
            "schemaVersion",
            "generatorId",
            "evidenceKind",
            "indexGeneration",
            "metadataRecordCount",
            "contentChunkCount",
            "lastEmbeddedMetadataCount",
            "lastEmbeddedContentChunkCount",
            "ingestEmbeddingsTotal",
            "queryEmbeddingsTotal",
            "indexIdentity",
            "effectiveRecommendationFloor",
            "appHostExpectedRecommendationFloor",
            "clientMemory",
            "networkBoundary");
        RequireInteger(root, "schemaVersion", 3);
        RequireString(
            root,
            "generatorId",
            "showcase-runtime-evidence-v3");
        RequireString(root, "evidenceKind", classification.Kind);
        RequirePositiveInteger(root, "indexGeneration");
        RequireInteger(root, "metadataRecordCount", 6);
        RequirePositiveInteger(root, "contentChunkCount");
        RequireNonNegativeInteger(root, "lastEmbeddedMetadataCount");
        RequireNonNegativeInteger(root, "lastEmbeddedContentChunkCount");
        RequirePositiveInteger(root, "ingestEmbeddingsTotal");
        RequireNonNegativeInteger(root, "queryEmbeddingsTotal");
        RequireSha256(root, "indexIdentity");
        RequirePositiveNumber(root, "effectiveRecommendationFloor");
        RequirePositiveNumber(root, "appHostExpectedRecommendationFloor");
        JsonElement client = RequireObject(root, "clientMemory");
        RequireExactProperties(
            client,
            "process",
            "units",
            "metricKind",
            "currentWorkingSetBytes",
            "peakWorkingSetBytes",
            "largestConversionRequest");
        RequireString(client, "process", "jellyfin-dotnet");
        RequireString(client, "units", "bytes");
        RequireString(
            client,
            "metricKind",
            "process-lifetime-high-water");
        RequirePositiveInteger(client, "currentWorkingSetBytes");
        RequirePositiveInteger(client, "peakWorkingSetBytes");
        JsonElement request =
            RequireObject(client, "largestConversionRequest");
        RequireExactProperties(
            request,
            "sourceBytes",
            "serializedRequestBytes",
            "beforeCurrentWorkingSetBytes",
            "beforePeakWorkingSetBytes",
            "afterCurrentWorkingSetBytes",
            "afterPeakWorkingSetBytes");
        foreach (string property in new[]
                 {
                     "sourceBytes",
                     "serializedRequestBytes",
                     "beforeCurrentWorkingSetBytes",
                     "beforePeakWorkingSetBytes",
                     "afterCurrentWorkingSetBytes",
                     "afterPeakWorkingSetBytes",
                 })
        {
            RequirePositiveInteger(request, property);
        }

        JsonElement boundary = RequireObject(root, "networkBoundary");
        RequireExactProperties(
            boundary,
            "externalHttpCanaryBlocked",
            "markItDownReachable",
            "markItDownReachabilityProbe",
            "enforcement",
            "residualLimitation");
        RequireBoolean(boundary, "externalHttpCanaryBlocked", true);
        RequireBoolean(boundary, "markItDownReachable", true);
        RequireNonEmptyString(
            boundary,
            "markItDownReachabilityProbe");
        RequireNonEmptyString(boundary, "enforcement");
        RequireNonEmptyString(boundary, "residualLimitation");
    }

    private static void ValidateServerMemoryEvidence(
        JsonElement root,
        EvidenceClassification classification)
    {
        RequireExactProperties(
            root,
            "schemaVersion",
            "generatorId",
            "evidenceKind",
            "interpretation",
            "observations");
        RequireInteger(root, "schemaVersion", 2);
        RequireString(
            root,
            "generatorId",
            "showcase-server-memory-evidence-v3");
        RequireString(root, "evidenceKind", classification.Kind);
        RequireNonEmptyString(root, "interpretation");
        JsonElement observations =
            RequireNonEmptyArray(root, "observations");
        foreach (JsonElement observation in observations.EnumerateArray())
        {
            RequireExactProperties(
                observation,
                "mediaType",
                "decodedSourceBytes",
                "sourceSha256",
                "process",
                "units",
                "metricKind",
                "processStartPeakRssKiB",
                "beforePeakRssKiB",
                "beforeCurrentRssKiB",
                "afterPeakRssKiB",
                "afterCurrentRssKiB");
            RequireNonEmptyString(observation, "mediaType");
            RequirePositiveInteger(observation, "decodedSourceBytes");
            RequireSha256(observation, "sourceSha256");
            RequireString(observation, "process", "markitdown-python");
            RequireString(observation, "units", "kib");
            RequireString(
                observation,
                "metricKind",
                "process_lifetime_high_water");
            foreach (string property in new[]
                     {
                         "processStartPeakRssKiB",
                         "beforePeakRssKiB",
                         "beforeCurrentRssKiB",
                         "afterPeakRssKiB",
                         "afterCurrentRssKiB",
                     })
            {
                RequirePositiveInteger(observation, property);
            }
        }
    }

    private static void ValidateNetworkEvidence(
        JsonElement root,
        EvidenceClassification classification)
    {
        RequireExactProperties(
            root,
            "schemaVersion",
            "generatorId",
            "evidenceKind",
            "jellyfin",
            "markItDown",
            "providerConfigurationAssertedBy");
        RequireInteger(root, "schemaVersion", 2);
        RequireString(
            root,
            "generatorId",
            "showcase-network-evidence-v3");
        RequireString(root, "evidenceKind", classification.Kind);
        JsonElement jellyfin = RequireObject(root, "jellyfin");
        RequireExactProperties(
            jellyfin,
            "enforcement",
            "residualLimitation",
            "initialExternalHttpCanaryBlocked",
            "restartExternalHttpCanaryBlocked",
            "initialMarkItDownReachable",
            "restartMarkItDownReachable",
            "markItDownReachabilityProbe");
        RequireNonEmptyString(jellyfin, "enforcement");
        RequireNonEmptyString(jellyfin, "residualLimitation");
        RequireBoolean(
            jellyfin,
            "initialExternalHttpCanaryBlocked",
            true);
        RequireBoolean(
            jellyfin,
            "restartExternalHttpCanaryBlocked",
            true);
        RequireBoolean(jellyfin, "initialMarkItDownReachable", true);
        RequireBoolean(jellyfin, "restartMarkItDownReachable", true);
        RequireNonEmptyString(
            jellyfin,
            "markItDownReachabilityProbe");
        JsonElement markItDown = RequireObject(root, "markItDown");
        RequireExactProperties(
            markItDown,
            "enforcement",
            "initial",
            "restart");
        RequireNonEmptyString(markItDown, "enforcement");
        foreach (string phase in new[] { "initial", "restart" })
        {
            JsonElement probe = RequireObject(markItDown, phase);
            RequireExactProperties(probe, "blocked", "probes");
            RequireBoolean(probe, "blocked", true);
            JsonElement probes = RequireObject(probe, "probes");
            RequireExactProperties(
                probes,
                "getaddrinfo",
                "gethostbyname",
                "gethostbyname_ex",
                "gethostbyaddr",
                "getnameinfo",
                "create_connection",
                "socket_connect",
                "socket_connect_ex",
                "socket_sendto",
                "socket_sendmsg");
            if (probes.EnumerateObject().Any(static property =>
                    property.Value.ValueKind != JsonValueKind.True))
            {
                throw new InvalidDataException(
                    "MarkItDown network evidence contains a failed probe.");
            }
        }

        RequireNonEmptyString(
            root,
            "providerConfigurationAssertedBy");
    }

    private static JsonElement RequireObject(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be an object.");
        }

        return value;
    }

    private static JsonElement RequireArray(
        JsonElement parent,
        string name,
        int count)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() != count)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must contain exactly {count} entries.");
        }

        return value;
    }

    private static JsonElement RequireNonEmptyArray(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be a non-empty array.");
        }

        return value;
    }

    private static JsonElement RequireProperty(
        JsonElement parent,
        string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' is required.");
        }

        return value;
    }

    private static void ValidateNativeItems(JsonElement items)
    {
        foreach (JsonElement item in items.EnumerateArray())
        {
            RequireExactProperties(item, "id", "name", "type");
            RequireNonEmptyString(item, "id");
            RequireNonEmptyString(item, "name");
            RequireNonEmptyString(item, "type");
        }
    }

    private static void RequireStringOrNull(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be null or a non-empty string.");
        }
    }

    private static string RequireNonEmptyString(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static void RequireString(
        JsonElement parent,
        string name,
        string expected)
    {
        if (!string.Equals(
                RequireNonEmptyString(parent, name),
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' has an unexpected value.");
        }
    }

    private static long RequirePositiveInteger(
        JsonElement parent,
        string name)
    {
        long value = RequireInteger(parent, name);
        if (value <= 0)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be positive.");
        }

        return value;
    }

    private static long RequireNonNegativeInteger(
        JsonElement parent,
        string name)
    {
        long value = RequireInteger(parent, name);
        if (value < 0)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be non-negative.");
        }

        return value;
    }

    private static long? RequireNullableInteger(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long number))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be null or an integer.");
        }

        return number;
    }

    private static long RequireInteger(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long number))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be an integer.");
        }

        return number;
    }

    private static void RequireInteger(
        JsonElement parent,
        string name,
        long expected)
    {
        if (RequireInteger(parent, name) != expected)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' has an unexpected value.");
        }
    }

    private static double RequireNumber(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double number) ||
            !double.IsFinite(number))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be finite.");
        }

        return number;
    }

    private static void RequirePositiveNumber(
        JsonElement parent,
        string name)
    {
        if (RequireNumber(parent, name) <= 0D)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be positive.");
        }
    }

    private static void RequireBoolean(
        JsonElement parent,
        string name,
        bool expected)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False) ||
            value.GetBoolean() != expected)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' has an unexpected Boolean value.");
        }
    }

    private static bool RequireBoolean(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequireProperty(parent, name);
        if (value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be Boolean.");
        }

        return value.GetBoolean();
    }

    private static string[] ValidateStringArray(
        JsonElement value,
        bool unique,
        int? expectedCount = null)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            (expectedCount.HasValue &&
             value.GetArrayLength() != expectedCount.Value))
        {
            throw new InvalidDataException(
                "Evidence string-array shape is invalid.");
        }

        string[] result = value.EnumerateArray()
            .Select(static item =>
                item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : null)
            .OfType<string>()
            .ToArray();
        if (result.Length != value.GetArrayLength() ||
            result.Any(string.IsNullOrWhiteSpace) ||
            (unique &&
             result.Distinct(StringComparer.Ordinal).Count() !=
                 result.Length))
        {
            throw new InvalidDataException(
                "Evidence string array is empty, invalid, or duplicated.");
        }

        return result;
    }

    private static void RequireSha256(
        JsonElement parent,
        string name)
    {
        string value = RequireNonEmptyString(parent, name);
        if (!IsHex(value, 64))
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be a lowercase SHA-256.");
        }
    }

    private static void RequireExactProperties(
        JsonElement value,
        params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Evidence value must be an object.");
        }

        string[] actual = value.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        if (actual.Length != expected.Length ||
            !actual.ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected))
        {
            throw new InvalidDataException(
                "Evidence object properties do not match its versioned schema.");
        }
    }

    private static void RequireAllowedProperties(
        JsonElement value,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Evidence value must be an object.");
        }

        HashSet<string> actual = value.EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> allowed = required
            .Concat(optional)
            .ToHashSet(StringComparer.Ordinal);
        if (!required.All(actual.Contains) ||
            actual.Any(property => !allowed.Contains(property)))
        {
            throw new InvalidDataException(
                "Evidence object properties do not match its versioned schema.");
        }
    }

    public static string ComputeImplementationContentSha256(
        string repositoryRoot)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string[] files = GetTrackedFiles(root)
            .Where(relative => IsImplementationInput(relative))
            .Select(relative => Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar)))
            .OrderBy(
                path => Path.GetRelativePath(root, path)
                    .Replace('\\', '/'),
                StringComparer.Ordinal)
            .ToArray();
        using IncrementalHash digest =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] length = new byte[sizeof(long)];
        foreach (string path in files)
        {
            string relative = Path.GetRelativePath(root, path)
                .Replace('\\', '/');
            byte[] pathBytes = Encoding.UTF8.GetBytes(relative);
            digest.AppendData(pathBytes);
            digest.AppendData([0]);
            byte[] content = ReadCanonicalImplementationBytes(path);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                length,
                content.LongLength);
            digest.AppendData(length);
            digest.AppendData(content);
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    internal static string ComputeCanonicalTextFileSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(
            ReadCanonicalImplementationBytes(path)));

    private static byte[] ReadCanonicalImplementationBytes(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        string extension = Path.GetExtension(path);
        if (extension is ".nfo" or ".opf" or ".srt" or ".vtt" or
            ".png" or ".pdf" or ".epub" or ".mp4" or ".onnx")
        {
            return content;
        }

        using MemoryStream canonical = new(content.Length);
        for (int index = 0; index < content.Length; index++)
        {
            if (content[index] == (byte)'\r' &&
                index + 1 < content.Length &&
                content[index + 1] == (byte)'\n')
            {
                continue;
            }

            canonical.WriteByte(content[index]);
        }

        return canonical.ToArray();
    }

    private static string[] GetTrackedFiles(string root)
    {
        ProcessStartInfo start = new("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Could not start git for evidence provenance.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not enumerate tracked evidence inputs: {error}");
        }

        return output.Split(
            '\0',
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsImplementationInput(string relative)
    {
        return !relative.StartsWith(
                "assets/showcase/evidence/",
                StringComparison.Ordinal);
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record EvidenceClassification(
        string Kind,
        string Proves,
        bool Deterministic);
}

public sealed record ShowcaseEvidenceManifest(
    int SchemaVersion,
    string GeneratorId,
    string ProfileId,
    string BaseCommit,
    string ImplementationContentSha256,
    string SourceLockSha256,
    IReadOnlyDictionary<string, string> GeneratorVersions,
    ShowcaseSanitizerPolicy SanitizerPolicy,
    IReadOnlyList<ShowcaseEvidenceFile> Artifacts);

public sealed record ShowcaseEvidenceFile(
    string Path,
    long Bytes,
    string Sha256,
    string EvidenceKind,
    string Proves,
    bool Deterministic);

public sealed record ShowcaseSanitizerPolicy(
    bool SourceBinariesIncluded,
    bool BulkExtractedBookTextIncluded,
    bool ContentChunksIncluded,
    bool VectorValuesIncluded,
    bool CredentialsIncluded,
    bool CookiesIncluded,
    bool AuthorizationHeadersIncluded,
    bool PayloadBodiesIncluded,
    bool RemoteArtworkIncluded,
    bool BookScreenshotsIncluded);
