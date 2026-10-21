using Jellyfin.SemanticDiscovery.Plugin;
using PluginClass = Jellyfin.SemanticDiscovery.Plugin.Plugin;

namespace Jellyfin.SemanticDiscovery.IntegrationTests;

public sealed class PluginConfigurationTests
{
    [Fact]
    public void ProductRecommendationFloorRemainsPointFiveByDefault()
    {
        PluginConfiguration configuration = new();

        Assert.Equal(0.5F, configuration.EffectiveRecommendationFloor);
        Assert.Equal(0.5F, configuration.LastRecommendationEffectiveFloor);
    }

    [Theory]
    [InlineData(50, 20, 20)]
    [InlineData(5, 20, 5)]
    [InlineData(null, 20, 20)]
    [InlineData(0, 20, 1)]
    [InlineData(-10, 20, 1)]
    [InlineData(500, 20, 20)]
    [InlineData(50, 0, 1)]
    [InlineData(50, 500, 50)]
    public void ResolveSearchLimitHonorsConfiguredMaximum(
        int? requested,
        int configured,
        int expected)
    {
        Assert.Equal(
            expected,
            SemanticSearchProvider.ResolveSearchLimit(
                requested,
                configured));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(101, 100)]
    [InlineData(500, 100)]
    public void MergeUserConfigurationNormalizesSearchLimit(
        int incomingLimit,
        int expected)
    {
        PluginConfiguration current = new()
        {
            IndexGeneration = 42,
            RecommendationQueryCount = 7,
            EffectiveRecommendationFloor = 0.38F,
            ClientProcessCurrentWorkingSetBytes = 101,
            ClientProcessPeakWorkingSetBytes = 102,
            LargestConversionSourceBytes = 103,
            LargestSerializedRequestBytes = 104,
            LargestRequestBeforeCurrentWorkingSetBytes = 105,
            LargestRequestBeforePeakWorkingSetBytes = 106,
            LargestRequestAfterCurrentWorkingSetBytes = 107,
            LargestRequestAfterPeakWorkingSetBytes = 108,
            ExternalEgressCanaryBlocked = true,
            MarkItDownReachable = true,
            MarkItDownReachabilityProbe = "exact MCP tools/list",
            EgressEnforcement = "proxy-policy",
            EgressResidualLimitation = "direct sockets",
            LastRecommendationSourceItemId = "source-id",
            LastRecommendationResultsJson = """[{"score":0.4}]""",
            LastRecommendationEffectiveFloor = 0.38F,
        };
        PluginConfiguration incoming = new()
        {
            SearchLimit = incomingLimit,
        };

        PluginConfiguration merged =
            PluginClass.MergeUserConfiguration(current, incoming);

        Assert.Equal(expected, merged.SearchLimit);
        Assert.Equal(42, merged.IndexGeneration);
        Assert.Equal(7, merged.RecommendationQueryCount);
        Assert.Equal(0.38F, merged.EffectiveRecommendationFloor);
        Assert.Equal(101, merged.ClientProcessCurrentWorkingSetBytes);
        Assert.Equal(102, merged.ClientProcessPeakWorkingSetBytes);
        Assert.Equal(103, merged.LargestConversionSourceBytes);
        Assert.Equal(104, merged.LargestSerializedRequestBytes);
        Assert.Equal(105, merged.LargestRequestBeforeCurrentWorkingSetBytes);
        Assert.Equal(106, merged.LargestRequestBeforePeakWorkingSetBytes);
        Assert.Equal(107, merged.LargestRequestAfterCurrentWorkingSetBytes);
        Assert.Equal(108, merged.LargestRequestAfterPeakWorkingSetBytes);
        Assert.True(merged.ExternalEgressCanaryBlocked);
        Assert.True(merged.MarkItDownReachable);
        Assert.Equal(
            "exact MCP tools/list",
            merged.MarkItDownReachabilityProbe);
        Assert.Equal("proxy-policy", merged.EgressEnforcement);
        Assert.Equal("direct sockets", merged.EgressResidualLimitation);
        Assert.Equal("source-id", merged.LastRecommendationSourceItemId);
        Assert.Equal(
            """[{"score":0.4}]""",
            merged.LastRecommendationResultsJson);
        Assert.Equal(0.38F, merged.LastRecommendationEffectiveFloor);
    }

    [Fact]
    public void CaptureSettingsProducesAnImmutableConsistentValueSnapshot()
    {
        PluginConfiguration configuration = new()
        {
            Enabled = true,
            SearchLimit = 17,
            ModelDirectory = "model-a",
            SnapshotPath = "snapshot-a",
            MarkItDownEndpoint = "http://markitdown-a/mcp",
        };

        PluginSettings captured =
            Jellyfin.SemanticDiscovery.Plugin.Plugin.CaptureSettings(
                configuration);
        configuration.Enabled = false;
        configuration.SearchLimit = 99;
        configuration.ModelDirectory = "model-b";
        configuration.SnapshotPath = "snapshot-b";
        configuration.MarkItDownEndpoint = "http://markitdown-b/mcp";

        Assert.True(captured.Enabled);
        Assert.Equal(17, captured.SearchLimit);
        Assert.Equal("model-a", captured.ModelDirectory);
        Assert.Equal("snapshot-a", captured.SnapshotPath);
        Assert.Equal("http://markitdown-a/mcp", captured.MarkItDownEndpoint);
    }

    [Fact]
    public void IsQuerySupportedRejectsOversizedTextWithoutInspectingItsContent()
    {
        Assert.True(SemanticSearchProvider.IsQuerySupported("ordinary query"));
        Assert.False(SemanticSearchProvider.IsQuerySupported(
            new string('x', SemanticSearchProvider.MaximumQueryScalars + 1)));
        Assert.False(SemanticSearchProvider.IsQuerySupported(
            string.Concat(
                Enumerable.Repeat(
                    "😀",
                    SemanticSearchProvider.MaximumQueryScalars + 1))));
    }
}
