using MediaBrowser.Model.Plugins;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public string ModelDirectory { get; set; } = string.Empty;

    public string SnapshotPath { get; set; } = string.Empty;

    public string MarkItDownEndpoint { get; set; } = string.Empty;

    public int SearchLimit { get; set; } = 20;

    public string LastStatus { get; set; } = "Not indexed";

    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset? LastUpdatedUtc { get; set; }

    public DateTimeOffset? LastSuccessfulRefreshUtc { get; set; }

    public long IndexGeneration { get; set; }

    public int MetadataRecordCount { get; set; }

    public int ContentChunkCount { get; set; }

    public int LastEmbeddedMetadataCount { get; set; }

    public int LastEmbeddedContentChunkCount { get; set; }

    public string IndexIdentity { get; set; } = string.Empty;

    public string ModelStatus { get; set; } = "Not validated";

    public long RecommendationQueryCount { get; set; }

    public long RecommendationResultCount { get; set; }

    public long IngestEmbeddingsTotal { get; set; }

    public long QueryEmbeddingsTotal { get; set; }

    public float EffectiveRecommendationFloor { get; set; } =
        new Jellyfin.SemanticDiscovery.Core.Retrieval
            .SearchScoreCalibration().RecommendationFloor;

    public long ClientProcessCurrentWorkingSetBytes { get; set; }

    public long ClientProcessPeakWorkingSetBytes { get; set; }

    public long LargestConversionSourceBytes { get; set; }

    public long LargestSerializedRequestBytes { get; set; }

    public long LargestRequestBeforeCurrentWorkingSetBytes { get; set; }

    public long LargestRequestBeforePeakWorkingSetBytes { get; set; }

    public long LargestRequestAfterCurrentWorkingSetBytes { get; set; }

    public long LargestRequestAfterPeakWorkingSetBytes { get; set; }

    public bool ExternalEgressCanaryBlocked { get; set; }

    public bool MarkItDownReachable { get; set; }

    public string MarkItDownReachabilityProbe { get; set; } =
        "not-checked";

    public string EgressEnforcement { get; set; } = "not-configured";

    public string EgressResidualLimitation { get; set; } =
        "No container-level egress boundary is configured.";

    public string LastRecommendationSourceItemId { get; set; } =
        string.Empty;

    public string LastRecommendationResultsJson { get; set; } = "[]";

    public float LastRecommendationEffectiveFloor { get; set; } =
        new Jellyfin.SemanticDiscovery.Core.Retrieval
            .SearchScoreCalibration().RecommendationFloor;
}
