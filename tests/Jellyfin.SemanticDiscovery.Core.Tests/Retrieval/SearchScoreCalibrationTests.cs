using Jellyfin.SemanticDiscovery.Core.Retrieval;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Retrieval;

public sealed class SearchScoreCalibrationTests
{
    [Fact]
    public void ValidateDoesNotThrowForDefaultCalibration()
    {
        SearchScoreCalibration calibration = new();

        calibration.Validate();
    }

    [Fact]
    public void ValidateThrowsWhenCeilingIsNotBelowExactTitleScore()
    {
        SearchScoreCalibration calibration = new() { SemanticScoreCeiling = 1F, ExactTitleScore = 1F };

        Assert.Throws<InvalidOperationException>(calibration.Validate);
    }

    [Theory]
    [InlineData(-0.01F)]
    [InlineData(1.01F)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ValidateThrowsWhenMetadataFloorIsOutOfUnitInterval(float floor)
    {
        SearchScoreCalibration calibration = new() { MetadataFloor = floor };

        Assert.Throws<InvalidOperationException>(calibration.Validate);
    }

    [Theory]
    [InlineData(-0.01F)]
    [InlineData(1.01F)]
    public void ValidateThrowsWhenContentFloorIsOutOfUnitInterval(float floor)
    {
        SearchScoreCalibration calibration = new() { ContentFloor = floor };

        Assert.Throws<InvalidOperationException>(calibration.Validate);
    }

    [Fact]
    public void ValidateThrowsWhenOverFetchMultiplierIsNotPositive()
    {
        SearchScoreCalibration calibration = new() { OverFetchMultiplier = 0 };

        Assert.Throws<InvalidOperationException>(calibration.Validate);
    }

    [Fact]
    public void ValidateThrowsWhenMinimumOverFetchIsNotPositive()
    {
        SearchScoreCalibration calibration = new() { MinimumOverFetch = 0 };

        Assert.Throws<InvalidOperationException>(calibration.Validate);
    }
}
