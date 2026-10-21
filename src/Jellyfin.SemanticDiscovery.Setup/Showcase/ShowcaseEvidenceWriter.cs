using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.VectorData;
using Microsoft.Extensions.AI;
using Jellyfin.SemanticDiscovery.Core.Snapshots;

namespace Jellyfin.SemanticDiscovery.Setup.Showcase;

public static partial class ShowcaseEvidenceWriter
{
    public const string GeneratorId =
        "showcase-direct-csharp-retrieval-evidence-v3";
    private const float CosineEpsilon = 0.0001F;
    private static readonly float DefaultRecommendationFloor =
        new SearchScoreCalibration().RecommendationFloor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    public static async Task<ShowcaseRetrievalEvidence> WriteAsync(
        string sourceLockPath,
        string snapshotPath,
        string showcaseRoot,
        string modelDirectory,
        string outputPath,
        float recommendationFloor,
        CancellationToken cancellationToken)
    {
        ShowcaseSourceLock sourceLock =
            await ShowcaseSourceLock.LoadAsync(
                sourceLockPath,
                cancellationToken).ConfigureAwait(false);
        if (!float.IsFinite(recommendationFloor) ||
            recommendationFloor is < 0F or > 1F)
        {
            throw new ArgumentOutOfRangeException(nameof(recommendationFloor));
        }
        IndexSnapshot snapshot =
            await new AtomicSnapshotStore(snapshotPath)
                .LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException(
                "The showcase semantic snapshot does not exist.",
                snapshotPath);
        Dictionary<string, PersistedMetadataVector> metadataByTitle =
            snapshot.Metadata.ToDictionary(
                static item => item.Record.Title,
                StringComparer.Ordinal);
        if (!sourceLock.Items.All(item =>
                metadataByTitle.ContainsKey(item.Title)) ||
            metadataByTitle.Count != sourceLock.Items.Length)
        {
            throw new InvalidDataException(
                "The semantic snapshot is not the exact six-item showcase profile.");
        }

        List<ShowcaseAnchorEvidence> anchors = [];
        foreach (ShowcaseItem item in sourceLock.Items)
        {
            PersistedMetadataVector owner = metadataByTitle[item.Title];
            if (!string.Equals(
                    owner.ProjectionHash,
                    item.DemoGenerated.ProjectedMetadataSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Jellyfin projection for '{item.Id}' drifted from the source lock.");
            }
            string canonicalContent = NormalizeAnchor(string.Join(
                ' ',
                snapshot.Content
                    .Where(content =>
                        content.Chunk.OwnerId == owner.Record.Id)
                    .OrderBy(static content =>
                        content.Chunk.SourcePath,
                        StringComparer.Ordinal)
                    .ThenBy(static content => content.Chunk.Ordinal)
                    .Select(static content => content.Chunk.Text)));
            foreach (ShowcaseAnchor anchor in item.Anchors)
            {
                string normalizedAnchor = NormalizeAnchor(anchor.Text);
                PersistedContentVector? matchingChunk = snapshot.Content
                    .Where(content =>
                        content.Chunk.OwnerId == owner.Record.Id)
                    .FirstOrDefault(content =>
                        NormalizeAnchor(content.Chunk.Text).Contains(
                            normalizedAnchor,
                            StringComparison.Ordinal));
                bool matched = matchingChunk is not null ||
                    canonicalContent.Contains(
                        normalizedAnchor,
                        StringComparison.Ordinal);
                if (!matched)
                {
                    throw new InvalidDataException(
                        $"Canonical showcase extraction omitted anchor at '{anchor.Location}'.");
                }

                ShowcaseArtifact anchorArtifact = item.Artifacts.Single(
                    artifact => artifact.Id == anchor.ArtifactId);
                SubtitleCue? cue = null;
                if (anchorArtifact.Role == "subtitle")
                {
                    cue = await ResolveExactSubtitleCueAsync(
                        item,
                        anchor,
                        anchorArtifact,
                        showcaseRoot,
                        cancellationToken).ConfigureAwait(false);
                    if (matchingChunk?.Chunk.StartTicks is null ||
                        matchingChunk.Chunk.EndTicks is null ||
                        matchingChunk.Chunk.StartTicks > cue.StartTicks ||
                        matchingChunk.Chunk.EndTicks < cue.EndTicks)
                    {
                        throw new InvalidDataException(
                            $"Subtitle anchor at '{anchor.Location}' is not contained by its indexed chunk.");
                    }
                }

                anchors.Add(new ShowcaseAnchorEvidence(
                    item.Id,
                    anchor.ArtifactSha256,
                    anchor.ExtractorIdentity,
                    anchor.Normalization,
                    anchor.Location,
                    Matched: true,
                    anchor.CueIndex,
                    cue?.StartTicks,
                    cue?.EndTicks,
                    matchingChunk?.Chunk.StartTicks,
                    matchingChunk?.Chunk.EndTicks));
            }
        }

        ShowcaseItemIdentityEvidence[] itemIdentities =
            sourceLock.Items
                .OrderBy(static item => item.Id, StringComparer.Ordinal)
                .Select(item =>
                {
                    PersistedMetadataVector metadata =
                        metadataByTitle[item.Title];
                    return new ShowcaseItemIdentityEvidence(
                        item.Id,
                        metadata.Record.Id,
                        metadata.ProjectionHash);
                })
                .ToArray();
        string snapshotContentId = ContentHash.Compute(string.Join(
            '\n',
            snapshot.Identity.CompositeHash,
            string.Join(
                '\n',
                itemIdentities.Select(static item =>
                    $"{item.LockItemId}:{item.JellyfinItemId:N}:{item.CanonicalProjectionSha256}")),
            string.Join(
                '\n',
                snapshot.Content
                    .OrderBy(static item => item.Chunk.Id, StringComparer.Ordinal)
                    .Select(static item =>
                        $"{item.Chunk.Id}:{item.Chunk.ContentHash}"))));
        List<ShowcaseRecommendationEvidence> recommendations = [];
        foreach (ShowcaseItem sourceItem in sourceLock.Items.OrderBy(
                     static item => item.Id,
                     StringComparer.Ordinal))
        {
            PersistedMetadataVector source = metadataByTitle[sourceItem.Title];
            ShowcaseRankedCandidate[] ranking = snapshot.Metadata
                .Where(candidate =>
                    candidate.Record.Id != source.Record.Id &&
                    candidate.Record.Kind == source.Record.Kind)
                .Select(candidate => new ShowcaseRankedCandidate(
                    sourceLock.Items.Single(item =>
                        item.Title == candidate.Record.Title).Id,
                    VectorMath.CosineSimilarity(
                        source.Vector,
                        candidate.Vector)))
                .OrderByDescending(static candidate => candidate.Similarity)
                .ThenBy(static candidate => candidate.ItemId, StringComparer.Ordinal)
                .ToArray();
            ShowcaseRankedCandidate top = ranking.FirstOrDefault()
                ?? throw new InvalidDataException(
                    $"Showcase recommendation source '{sourceItem.Id}' has no same-type candidates.");
            bool eligibleAtConfiguredFloor =
                top.Similarity > recommendationFloor;
            bool eligibleAtDefaultFloor =
                top.Similarity > DefaultRecommendationFloor;
            if (eligibleAtDefaultFloor)
            {
                throw new InvalidDataException(
                    $"Showcase source '{sourceItem.Id}' unexpectedly clears the default recommendation floor.");
            }

            recommendations.Add(new ShowcaseRecommendationEvidence(
                sourceItem.Id,
                eligibleAtConfiguredFloor ? top.ItemId : null,
                eligibleAtConfiguredFloor ? 1 : null,
                top.Similarity,
                DefaultRecommendationFloor,
                recommendationFloor,
                eligibleAtDefaultFloor,
                eligibleAtConfiguredFloor,
                ranking));
        }

        ShowcaseSimilarityMatrixEvidence[] matrices =
            Enum.GetValues<MediaKind>()
                .Where(kind => kind is MediaKind.Movie or MediaKind.Book)
                .Select(kind => CreateMatrix(
                    kind,
                    sourceLock,
                    metadataByTitle,
                    recommendationFloor))
                .ToArray();
        List<ShowcaseRequestSizeEvidence> requestSizes = [];
        foreach (ShowcaseItem item in sourceLock.Items.Where(
                     static item => item.JellyfinType == "Book"))
        {
            ShowcaseArtifact artifact = item.Artifacts.Single(
                static value => value.Role == "book");
            string folder = Path.Combine(
                Path.GetFullPath(showcaseRoot),
                "library",
                "Books",
                item.DemoGenerated.FolderName,
                artifact.FileName);
            byte[] bytes = await File.ReadAllBytesAsync(
                folder,
                cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength != artifact.Size ||
                !string.Equals(
                    Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(bytes)),
                    artifact.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Request-size evidence source '{artifact.Id}' drifted from its lock.");
            }
            string mediaType = artifact.FileName.EndsWith(
                ".epub",
                StringComparison.OrdinalIgnoreCase)
                ? "application/epub+zip"
                : "application/pdf";
            int requestBytes =
                MarkItDownMcpReader.MeasureSerializedRequestBytes(
                    bytes,
                    mediaType);
            if (requestBytes >
                MarkItDownMcpReader.AbsoluteMaximumRequestBytes)
            {
                throw new InvalidDataException(
                    $"Serialized request for '{artifact.Id}' exceeds the hard cap.");
            }
            requestSizes.Add(new ShowcaseRequestSizeEvidence(
                artifact.Id,
                artifact.Size,
                requestBytes,
                MarkItDownMcpReader.AbsoluteMaximumRequestBytes));
        }
        List<ShowcaseQueryEvidence> queries = [];
        using (MiniLmEmbeddingGenerator generator = new(
                   new MiniLmEmbeddingOptions
                   {
                       ModelPath = Path.Combine(
                           modelDirectory,
                           "model.onnx"),
                       VocabularyPath = Path.Combine(
                           modelDirectory,
                           "vocab.txt"),
                       MaxBatchSize = 32,
                       MaxInputsPerRequest = 32,
                       IntraOpThreads = 2,
                   }))
        {
            if (generator.Identity.CompositeHash !=
                snapshot.Identity.CompositeHash)
            {
                throw new InvalidDataException(
                    "Showcase evidence generator identity does not match the snapshot.");
            }

            InMemorySemanticVectorIndex vectorIndex = new();
            await vectorIndex.ReplaceAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            SemanticDiscoveryService discovery = new(
                generator,
                vectorIndex,
                new AllowAllAccessPolicy(),
                new SearchScoreCalibration
                {
                    MetadataFloor = 0F,
                    ContentFloor = 0F,
                    RecommendationFloor = recommendationFloor,
                });
            foreach (ShowcaseItem item in sourceLock.Items)
            {
                string query = item.Metadata.QueryToMeasure ??
                    item.Anchors.Select(static anchor =>
                            anchor.QueryToMeasure)
                        .FirstOrDefault(static value =>
                            !string.IsNullOrWhiteSpace(value))
                    ?? throw new InvalidDataException(
                        $"Showcase item '{item.Id}' has no measured query.");
                SearchScope queryScope = new()
                {
                    UserId = Guid.Parse(
                        "11111111-1111-1111-1111-111111111111"),
                    Kinds = new HashSet<MediaKind>
                    {
                        item.JellyfinType == "Movie"
                            ? MediaKind.Movie
                            : MediaKind.Book,
                    },
                };
                IReadOnlyList<SemanticSearchResult> results =
                    await discovery.SearchAsync(
                        query,
                        queryScope,
                        limit: 6,
                        cancellationToken).ConfigureAwait(false);
                IReadOnlyList<SemanticSearchResult> repeated =
                    await discovery.SearchAsync(
                        query,
                        queryScope,
                        limit: 6,
                        cancellationToken).ConfigureAwait(false);
                ShowcaseRankedSearchResult[] ranked = results
                    .Select(result => new ShowcaseRankedSearchResult(
                        sourceLock.Items.Single(candidate =>
                            candidate.Title == result.Title).Id,
                        result.Kind.ToString(),
                        result.Score))
                    .ToArray();
                if (!ranked.Select(static result => result.ItemId)
                        .SequenceEqual(
                            repeated.Select(result =>
                                sourceLock.Items.Single(candidate =>
                                    candidate.Title == result.Title).Id)) ||
                    ranked.Zip(
                            repeated,
                            static (left, right) =>
                                MathF.Abs(left.Score - right.Score))
                        .Any(delta => delta > 0.0001F))
                {
                    throw new InvalidDataException(
                        $"Showcase query for '{item.Id}' was not deterministic within epsilon.");
                }
                int intendedRank = Array.FindIndex(
                    ranked,
                    result => result.ItemId == item.Id) + 1;
                if (intendedRank != 1)
                {
                    throw new InvalidDataException(
                        $"Showcase query for '{item.Id}' did not rank its owner first.");
                }

                float intendedScore = ranked[0].Score;
                float highestOtherScore = ranked
                    .Skip(1)
                    .Select(static result => result.Score)
                    .DefaultIfEmpty(0F)
                    .Max();
                float signedMargin =
                    intendedScore - highestOtherScore;
                if (!float.IsFinite(signedMargin) ||
                    signedMargin <= CosineEpsilon)
                {
                    throw new InvalidDataException(
                        $"Showcase query for '{item.Id}' has no positive top-rank margin.");
                }
                queries.Add(new ShowcaseQueryEvidence(
                    item.Id,
                    query,
                    intendedRank,
                    intendedScore,
                    highestOtherScore,
                    signedMargin,
                    ranked));
            }
        }

        ShowcaseRetrievalEvidence evidence = new(
            SchemaVersion: 3,
            GeneratorId,
            EvidenceKind: "direct-csharp-in-memory-vector-store",
            AccessControlBoundary:
                "AllowAllAccessPolicy and zero measurement floors expose the complete locked same-type distractor order; synthetic E2E is the access-control oracle.",
            ProjectionHashContract:
                MetadataProjectionIdentity.ContractId,
            MeasurementMetadataFloor: 0F,
            MeasurementContentFloor: 0F,
            sourceLock.ProfileId,
            snapshot.Identity.CompositeHash,
            snapshotContentId,
            ComputeVectorPayloadHash(snapshot),
            CosineEpsilon,
            snapshot.Metadata.Length,
            snapshot.Content.Length,
            recommendationFloor,
            anchors,
            recommendations,
            matrices,
            requestSizes,
            queries,
            itemIdentities);
        string? directory = Path.GetDirectoryName(
            Path.GetFullPath(outputPath));
        if (directory is null)
        {
            throw new InvalidOperationException(
                "Showcase evidence requires a parent directory.");
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(
            outputPath,
            JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    private static async Task<SubtitleCue> ResolveExactSubtitleCueAsync(
        ShowcaseItem item,
        ShowcaseAnchor anchor,
        ShowcaseArtifact artifact,
        string showcaseRoot,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            Path.GetFullPath(showcaseRoot),
            "library",
            "Movies",
            item.DemoGenerated.FolderName,
            artifact.FileName);
        if (!await ShowcaseInstaller.IsValidAsync(
                path,
                artifact,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                $"Subtitle source for '{item.Id}' failed its exact lock.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader text = new(stream, leaveOpen: false);
        IReadOnlyList<SubtitleCue> cues =
            await SubtitleCueReader.ReadAsync(
                text,
                cancellationToken).ConfigureAwait(false);
        int cueIndex = anchor.CueIndex
            ?? throw new InvalidDataException(
                $"Subtitle anchor '{anchor.Location}' has no cue index.");
        if (cueIndex > cues.Count)
        {
            throw new InvalidDataException(
                $"Subtitle anchor '{anchor.Location}' exceeds the parsed cue count.");
        }

        SubtitleCue cue = cues[cueIndex - 1];
        if (cue.StartTicks != anchor.CueStartTicks ||
            cue.EndTicks != anchor.CueEndTicks ||
            !string.Equals(
                NormalizeAnchor(cue.Text),
                NormalizeAnchor(anchor.Text),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Subtitle cue '{anchor.Location}' drifted from its exact text or timing.");
        }

        return cue;
    }

    internal static string NormalizeAnchor(string value) =>
        Whitespace().Replace(
            value.Normalize(NormalizationForm.FormC),
            " ").Trim();

    private static ShowcaseSimilarityMatrixEvidence CreateMatrix(
        MediaKind kind,
        ShowcaseSourceLock sourceLock,
        Dictionary<string, PersistedMetadataVector> metadataByTitle,
        float floor)
    {
        ShowcaseItem[] items = sourceLock.Items
            .Where(item => item.JellyfinType == kind.ToString())
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        float[][] rows = items.Select(source =>
            items.Select(target => Math.Clamp(
                VectorMath.CosineSimilarity(
                    metadataByTitle[source.Title].Vector,
                    metadataByTitle[target.Title].Vector),
                -1F,
                1F)).ToArray()).ToArray();
        float[] offDiagonal = rows
            .SelectMany((row, rowIndex) => row.Where(
                (_, columnIndex) => columnIndex != rowIndex))
            .ToArray();
        if (!offDiagonal.Any(score => score > floor) ||
            !offDiagonal.Any(score => score <= floor) ||
            offDiagonal.Distinct().Count() == 1)
        {
            throw new InvalidDataException(
                $"The {kind} showcase matrix does not prove both ranking and exclusion.");
        }

        Dictionary<string, string[]> ordering = items.ToDictionary(
            static item => item.Id,
            source =>
            {
                int sourceIndex = Array.IndexOf(items, source);
                return items.Select((item, index) => (item.Id, index))
                    .Where(entry => entry.index != sourceIndex)
                    .OrderByDescending(entry => rows[sourceIndex][entry.index])
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                    .Select(static entry => entry.Id)
                    .ToArray();
            },
            StringComparer.Ordinal);
        return new ShowcaseSimilarityMatrixEvidence(
            kind.ToString(),
            items.Select(static item => item.Id).ToArray(),
            rows,
            ordering);
    }

    private static string ComputeVectorPayloadHash(IndexSnapshot snapshot)
    {
        using System.Security.Cryptography.IncrementalHash hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        foreach (PersistedMetadataVector item in snapshot.Metadata.OrderBy(
                     static item => item.Record.Id))
        {
            foreach (float value in item.Vector)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                    bytes,
                    value);
                hash.AppendData(bytes);
            }
        }
        foreach (PersistedContentVector item in snapshot.Content.OrderBy(
                     static item => item.Chunk.Id,
                     StringComparer.Ordinal))
        {
            foreach (float value in item.Vector)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                    bytes,
                    value);
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private sealed class AllowAllAccessPolicy : IMediaAccessPolicy
    {
        public ValueTask<IReadOnlySet<Guid>> GetAccessibleItemIdsAsync(
            IReadOnlyList<MetadataRecord> candidates,
            SearchScope scope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlySet<Guid>>(
                candidates.Select(static item => item.Id).ToHashSet());
    }
}

public sealed record ShowcaseRetrievalEvidence(
    int SchemaVersion,
    string GeneratorId,
    string EvidenceKind,
    string AccessControlBoundary,
    string ProjectionHashContract,
    float MeasurementMetadataFloor,
    float MeasurementContentFloor,
    string ProfileId,
    string IndexIdentity,
    string SnapshotContentId,
    string VectorPayloadSha256,
    float CosineEpsilon,
    int MetadataCount,
    int ContentChunkCount,
    float RecommendationFloor,
    IReadOnlyList<ShowcaseAnchorEvidence> Anchors,
    IReadOnlyList<ShowcaseRecommendationEvidence> Recommendations,
    IReadOnlyList<ShowcaseSimilarityMatrixEvidence> Matrices,
    IReadOnlyList<ShowcaseRequestSizeEvidence> RequestSizes,
    IReadOnlyList<ShowcaseQueryEvidence> Queries,
    IReadOnlyList<ShowcaseItemIdentityEvidence> Items);

public sealed record ShowcaseAnchorEvidence(
    string ItemId,
    string ArtifactSha256,
    string ExtractorIdentity,
    string Normalization,
    string Location,
    bool Matched,
    int? CueIndex,
    long? CueStartTicks,
    long? CueEndTicks,
    long? ContainingChunkStartTicks,
    long? ContainingChunkEndTicks);

public sealed record ShowcaseRecommendationEvidence(
    string SourceItemId,
    string? ExpectedItemId,
    int? ExpectedRank,
    float TopCandidateSimilarity,
    float DefaultFloor,
    float ConfiguredFloor,
    bool EligibleAtDefaultFloor,
    bool EligibleAtConfiguredFloor,
    IReadOnlyList<ShowcaseRankedCandidate> Ranking);

public sealed record ShowcaseRankedCandidate(
    string ItemId,
    float Similarity);

public sealed record ShowcaseSimilarityMatrixEvidence(
    string ItemType,
    string[] ItemIds,
    float[][] Cosine,
    IReadOnlyDictionary<string, string[]> CandidateOrder);

public sealed record ShowcaseRequestSizeEvidence(
    string ArtifactId,
    long SourceBytes,
    int SerializedRequestBytes,
    int HardRequestCapBytes);

public sealed record ShowcaseQueryEvidence(
    string IntendedItemId,
    string Query,
    int IntendedRank,
    float IntendedScore,
    float HighestOtherScore,
    float SignedMarginOverHighestOther,
    IReadOnlyList<ShowcaseRankedSearchResult> Results);

public sealed record ShowcaseRankedSearchResult(
    string ItemId,
    string ItemType,
    float Score);

public sealed record ShowcaseItemIdentityEvidence(
    string LockItemId,
    Guid JellyfinItemId,
    string CanonicalProjectionSha256);
