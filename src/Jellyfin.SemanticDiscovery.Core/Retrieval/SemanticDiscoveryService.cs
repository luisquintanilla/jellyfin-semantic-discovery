using Jellyfin.SemanticDiscovery.Core.Domain;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

public sealed class SemanticDiscoveryService(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IVectorSearchIndex index,
    IMediaAccessPolicy accessPolicy,
    SearchScoreCalibration calibration)
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator =
        embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));

    private readonly IVectorSearchIndex _index =
        index ?? throw new ArgumentNullException(nameof(index));

    private readonly IMediaAccessPolicy _accessPolicy =
        accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy));

    private readonly SearchScoreCalibration _calibration =
        calibration ?? throw new ArgumentNullException(nameof(calibration));

    private readonly DeterministicScoreFusion _fusion = new(calibration);

    public async ValueTask<IReadOnlyList<SemanticSearchResult>> SearchAsync(
        string query,
        SearchScope scope,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _calibration.Validate();

        GeneratedEmbeddings<Embedding<float>> generated =
            await _embeddingGenerator.GenerateAsync(
                [query],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<float> queryVector = generated.Single().Vector;
        int overFetch = Math.Max(
            _calibration.MinimumOverFetch,
            checked(limit * _calibration.OverFetchMultiplier));
        IVectorSearchReadSession readSession = _index.OpenReadSession();
        IReadOnlyList<MetadataRecord> recordsInGeneration =
            await readSession.GetMetadataRecordsAsync(cancellationToken)
                .ConfigureAwait(false);
        MetadataRecord[] structurallyEligible = recordsInGeneration
            .Where(scope.MatchesStructure)
            .ToArray();
        IReadOnlySet<Guid> accessible =
            await _accessPolicy.GetAccessibleItemIdsAsync(
                structurallyEligible,
                scope,
                cancellationToken).ConfigureAwait(false);

        Task<IReadOnlyList<MetadataMatch>> metadataTask =
            readSession.SearchMetadataAsync(
                queryVector,
                overFetch,
                accessible,
                cancellationToken).AsTask();
        Task<IReadOnlyList<ContentMatch>> contentTask =
            readSession.SearchContentAsync(
                queryVector,
                overFetch,
                accessible,
                cancellationToken).AsTask();
        await Task.WhenAll(metadataTask, contentTask).ConfigureAwait(false);

        IReadOnlyList<MetadataMatch> metadataMatches =
            await metadataTask.ConfigureAwait(false);
        IReadOnlyList<ContentMatch> contentMatches =
            await contentTask.ConfigureAwait(false);

        Dictionary<Guid, MetadataRecord> records = metadataMatches
            .Select(static match => match.Record)
            .ToDictionary(static record => record.Id);
        foreach (Guid ownerId in contentMatches
                     .Select(static match => match.Chunk.OwnerId)
                     .Distinct()
                     .Where(ownerId => !records.ContainsKey(ownerId)))
        {
            MetadataRecord? record =
                await readSession.GetMetadataAsync(ownerId, cancellationToken)
                    .ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(ownerId, record);
            }
        }

        IReadOnlyList<SemanticSearchResult> fused =
            _fusion.Fuse(query, metadataMatches, contentMatches, records);
        return fused
            .Where(result => accessible.Contains(result.ItemId))
            .Take(limit)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<SimilarItemResult>> GetSimilarAsync(
        Guid sourceItemId,
        SearchScope scope,
        IReadOnlySet<Guid> excludedItemIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(excludedItemIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _calibration.Validate();
        IVectorSearchReadSession readSession = _index.OpenReadSession();

        MetadataRecord? source =
            await readSession.GetMetadataAsync(sourceItemId, cancellationToken)
                .ConfigureAwait(false);
        ReadOnlyMemory<float>? sourceVector =
            await readSession.GetMetadataVectorAsync(sourceItemId, cancellationToken)
                .ConfigureAwait(false);
        if (source is null || sourceVector is null)
        {
            return [];
        }

        IReadOnlyList<MetadataRecord> recordsInGeneration =
            await readSession.GetMetadataRecordsAsync(cancellationToken)
                .ConfigureAwait(false);
        MetadataRecord[] candidates = recordsInGeneration
            .Where(item =>
                item.Id != sourceItemId &&
                item.Kind == source.Kind &&
                !excludedItemIds.Contains(item.Id) &&
                scope.MatchesStructure(item))
            .DistinctBy(static item => item.Id)
            .ToArray();
        IReadOnlySet<Guid> accessible =
            await _accessPolicy.GetAccessibleItemIdsAsync(
                candidates,
                scope,
                cancellationToken).ConfigureAwait(false);

        MetadataRecord[] orderedCandidates = candidates
            .Where(item => accessible.Contains(item.Id))
            .OrderBy(static item => item.Id)
            .ToArray();
        IReadOnlyDictionary<Guid, ReadOnlyMemory<float>> candidateVectors =
            await readSession.GetMetadataVectorsAsync(
                orderedCandidates.Select(static item => item.Id),
                cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, MetadataRecord> records =
            orderedCandidates.ToDictionary(static item => item.Id);
        IReadOnlyList<(Guid ItemId, float Similarity)> scores;
        if (candidateVectors.Count == orderedCandidates.Length)
        {
            RecommendationMatrix matrix = RecommendationMatrix.Create(
                orderedCandidates,
                candidateVectors);
            scores = matrix.Score(sourceVector.Value.Span);
        }
        else
        {
            int overFetch = Math.Max(
                _calibration.MinimumOverFetch,
                checked(limit * _calibration.OverFetchMultiplier));
            IReadOnlyList<MetadataMatch> matches =
                await readSession.SearchMetadataAsync(
                    sourceVector.Value,
                    overFetch,
                    accessible,
                    cancellationToken).ConfigureAwait(false);
            scores = matches
                .Where(match => records.ContainsKey(match.Record.Id))
                .Select(static match => (
                    match.Record.Id,
                    match.Similarity))
                .ToArray();
        }

        return scores
            .Where(result =>
                float.IsFinite(result.Similarity) &&
                result.Similarity > _calibration.RecommendationFloor)
            .OrderByDescending(static result => result.Similarity)
            .ThenBy(
                result => records[result.ItemId].Title,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.ItemId)
            .Take(limit)
            .Select(result => new SimilarItemResult(
                result.ItemId,
                records[result.ItemId].Kind,
                records[result.ItemId].Title,
                result.Similarity))
            .ToArray();
    }
}
