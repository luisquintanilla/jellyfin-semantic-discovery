using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

public sealed class DeterministicScoreFusion(SearchScoreCalibration calibration)
{
    private readonly SearchScoreCalibration _calibration =
        calibration ?? throw new ArgumentNullException(nameof(calibration));

    public IReadOnlyList<SemanticSearchResult> Fuse(
        string query,
        IReadOnlyList<MetadataMatch> metadataMatches,
        IReadOnlyList<ContentMatch> contentMatches,
        IReadOnlyDictionary<Guid, MetadataRecord> contentOwners)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(metadataMatches);
        ArgumentNullException.ThrowIfNull(contentMatches);
        ArgumentNullException.ThrowIfNull(contentOwners);
        _calibration.Validate();

        Dictionary<Guid, Aggregate> aggregates = [];
        foreach (MetadataMatch match in metadataMatches)
        {
            Aggregate aggregate = GetOrCreate(aggregates, match.Record);
            aggregate.MetadataScore = Math.Max(
                aggregate.MetadataScore,
                Calibrate(match.Similarity, _calibration.MetadataFloor));
        }

        foreach (IGrouping<Guid, ContentMatch> ownerMatches in contentMatches
                     .GroupBy(static match => match.Chunk.OwnerId))
        {
            if (!contentOwners.TryGetValue(ownerMatches.Key, out MetadataRecord? record))
            {
                continue;
            }

            Aggregate aggregate = GetOrCreate(aggregates, record);
            float[] topScores = ownerMatches
                .Select(match => Calibrate(match.Similarity, _calibration.ContentFloor))
                .Where(static score => score > 0F)
                .OrderDescending()
                .Take(3)
                .ToArray();

            if (topScores.Length > 0)
            {
                aggregate.ContentScore = Math.Clamp(
                    topScores[0] +
                    (topScores.Skip(1).Sum() * 0.1F),
                    0F,
                    1F);
            }
        }

        string normalizedQuery = TextNormalization.NormalizeForIdentity(query);
        return aggregates.Values
            .Select(aggregate => ToResult(aggregate, normalizedQuery))
            .Where(static result => result.Score > 0F)
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.ItemId)
            .ToArray();
    }

    private SemanticSearchResult ToResult(Aggregate aggregate, string normalizedQuery)
    {
        bool exactTitle = string.Equals(
            TextNormalization.NormalizeForIdentity(aggregate.Record.Title),
            normalizedQuery,
            StringComparison.Ordinal);

        float combined = 1F -
            ((1F - aggregate.MetadataScore) * (1F - aggregate.ContentScore));
        float score = exactTitle
            ? _calibration.ExactTitleScore
            : Math.Min(combined, _calibration.SemanticScoreCeiling);

        return new SemanticSearchResult(
            aggregate.Record.Id,
            aggregate.Record.Kind,
            aggregate.Record.Title,
            score);
    }

    private static Aggregate GetOrCreate(
        IDictionary<Guid, Aggregate> aggregates,
        MetadataRecord record)
    {
        if (!aggregates.TryGetValue(record.Id, out Aggregate? aggregate))
        {
            aggregate = new Aggregate(record);
            aggregates.Add(record.Id, aggregate);
        }

        return aggregate;
    }

    private static float Calibrate(float similarity, float floor)
    {
        if (!float.IsFinite(similarity) || similarity <= floor)
        {
            return 0F;
        }

        return Math.Clamp((similarity - floor) / (1F - floor), 0F, 1F);
    }

    private sealed class Aggregate(MetadataRecord record)
    {
        public MetadataRecord Record { get; } = record;

        public float MetadataScore { get; set; }

        public float ContentScore { get; set; }
    }
}
