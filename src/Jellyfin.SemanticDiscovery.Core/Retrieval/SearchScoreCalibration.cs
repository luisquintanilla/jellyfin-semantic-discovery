namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

public sealed record SearchScoreCalibration
{
    public float MetadataFloor { get; init; } = 0.34F;

    public float ContentFloor { get; init; } = 0.38F;

    public float SemanticScoreCeiling { get; init; } = 0.89F;

    public float RecommendationFloor { get; init; } = 0.5F;

    public float ExactTitleScore { get; init; } = 1F;

    public int OverFetchMultiplier { get; init; } = 6;

    public int MinimumOverFetch { get; init; } = 32;

    public void Validate()
    {
        ValidateUnitInterval(MetadataFloor, nameof(MetadataFloor));
        ValidateUnitInterval(ContentFloor, nameof(ContentFloor));
        ValidateUnitInterval(SemanticScoreCeiling, nameof(SemanticScoreCeiling));
        ValidateUnitInterval(RecommendationFloor, nameof(RecommendationFloor));
        ValidateUnitInterval(ExactTitleScore, nameof(ExactTitleScore));
        if (SemanticScoreCeiling >= ExactTitleScore)
        {
            throw new InvalidOperationException(
                "The semantic score ceiling must remain below the exact-title score.");
        }

        if (OverFetchMultiplier < 1)
        {
            throw new InvalidOperationException("Over-fetch multiplier must be positive.");
        }

        if (MinimumOverFetch < 1)
        {
            throw new InvalidOperationException("Minimum over-fetch must be positive.");
        }
    }

    private static void ValidateUnitInterval(float value, string name)
    {
        if (!float.IsFinite(value) || value is < 0F or > 1F)
        {
            throw new InvalidOperationException($"{name} must be within [0, 1].");
        }
    }
}
