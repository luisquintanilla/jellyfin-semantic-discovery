using Jellyfin.Data.Enums;
using Jellyfin.SemanticDiscovery.Core.Domain;
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
            ExternalEgressCanaryBlocked = true,
            OllamaReachable = true,
            OllamaReachabilityProbe = "exact /api/tags",
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
        Assert.True(merged.ExternalEgressCanaryBlocked);
        Assert.True(merged.OllamaReachable);
        Assert.Equal(
            "exact /api/tags",
            merged.OllamaReachabilityProbe);
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
            OllamaEndpoint = "http://ollama-a:11434",
            SnapshotPath = "snapshot-a",
            EmbeddingModel = "embedding-a",
            ExtractionModel = "vision-a",
            ExtractionMode = "Hybrid",
            ExtractionArtifactRoot = "artifacts-a",
        };

        PluginSettings captured =
            Jellyfin.SemanticDiscovery.Plugin.Plugin.CaptureSettings(
                configuration);
        configuration.Enabled = false;
        configuration.SearchLimit = 99;
        configuration.OllamaEndpoint = "http://ollama-b:11434";
        configuration.SnapshotPath = "snapshot-b";
        configuration.EmbeddingModel = "embedding-b";
        configuration.ExtractionModel = "vision-b";
        configuration.ExtractionMode = "Text";
        configuration.ExtractionArtifactRoot = "artifacts-b";

        Assert.True(captured.Enabled);
        Assert.Equal(17, captured.SearchLimit);
        Assert.Equal(
            "http://ollama-a:11434",
            captured.OllamaEndpoint);
        Assert.Equal("snapshot-a", captured.SnapshotPath);
        Assert.Equal("embedding-a", captured.EmbeddingModel);
        Assert.Equal("vision-a", captured.ExtractionModel);
        Assert.Equal("Hybrid", captured.ExtractionMode);
        Assert.Equal("artifacts-a", captured.ExtractionArtifactRoot);
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

    [Fact]
    public void TypedSearchIncludesAudio()
    {
        HashSet<MediaKind> kinds = SemanticSearchProvider.GetKinds(
            [BaseItemKind.Audio],
            []);

        Assert.Equal([MediaKind.Audio], kinds);
    }
}
