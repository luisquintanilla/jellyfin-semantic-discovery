using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly Lock _configurationMutationLock = new();

    public static readonly Guid PluginId =
        Guid.Parse("de965148-5789-4e8c-9774-6d1409ef3478");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Semantic Discovery";

    public override string Description =>
        "Local semantic metadata and full-content discovery for Jellyfin.";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = Name,
            EmbeddedResourcePath =
                $"{GetType().Namespace}.Configuration.configPage.html",
            EnableInMainMenu = true,
        };
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not PluginConfiguration incoming)
        {
            throw new ArgumentException(
                $"Configuration must be {nameof(PluginConfiguration)}.",
                nameof(configuration));
        }

        lock (_configurationMutationLock)
        {
            base.UpdateConfiguration(MergeUserConfiguration(Configuration, incoming));
        }
    }

    internal void UpdateRuntimeConfiguration(
        Action<PluginConfiguration> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_configurationMutationLock)
        {
            PluginConfiguration configuration = Configuration;
            mutation(configuration);
            SaveConfiguration(configuration);
        }
    }

    internal TResult ReadConfiguration<TResult>(
        Func<PluginConfiguration, TResult> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_configurationMutationLock)
        {
            return read(Configuration);
        }
    }

    internal PluginSettings ReadSettings() =>
        ReadConfiguration(CaptureSettings);

    internal static PluginSettings CaptureSettings(
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new PluginSettings(
            configuration.Enabled,
            configuration.ModelDirectory,
            configuration.SnapshotPath,
            configuration.MarkItDownEndpoint,
            Math.Clamp(configuration.SearchLimit, 1, 100));
    }

    internal static PluginConfiguration MergeUserConfiguration(
        PluginConfiguration current,
        PluginConfiguration incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        return new PluginConfiguration
        {
            Enabled = incoming.Enabled,
            ModelDirectory = incoming.ModelDirectory,
            SnapshotPath = incoming.SnapshotPath,
            MarkItDownEndpoint = incoming.MarkItDownEndpoint,
            SearchLimit = Math.Clamp(incoming.SearchLimit, 1, 100),
            LastStatus = current.LastStatus,
            LastError = current.LastError,
            LastUpdatedUtc = current.LastUpdatedUtc,
            LastSuccessfulRefreshUtc = current.LastSuccessfulRefreshUtc,
            IndexGeneration = current.IndexGeneration,
            MetadataRecordCount = current.MetadataRecordCount,
            ContentChunkCount = current.ContentChunkCount,
            LastEmbeddedMetadataCount = current.LastEmbeddedMetadataCount,
            LastEmbeddedContentChunkCount =
                current.LastEmbeddedContentChunkCount,
            IndexIdentity = current.IndexIdentity,
            ModelStatus = current.ModelStatus,
            RecommendationQueryCount = current.RecommendationQueryCount,
            RecommendationResultCount = current.RecommendationResultCount,
            IngestEmbeddingsTotal = current.IngestEmbeddingsTotal,
            QueryEmbeddingsTotal = current.QueryEmbeddingsTotal,
            EffectiveRecommendationFloor =
                current.EffectiveRecommendationFloor,
            ClientProcessCurrentWorkingSetBytes =
                current.ClientProcessCurrentWorkingSetBytes,
            ClientProcessPeakWorkingSetBytes =
                current.ClientProcessPeakWorkingSetBytes,
            LargestConversionSourceBytes =
                current.LargestConversionSourceBytes,
            LargestSerializedRequestBytes =
                current.LargestSerializedRequestBytes,
            LargestRequestBeforeCurrentWorkingSetBytes =
                current.LargestRequestBeforeCurrentWorkingSetBytes,
            LargestRequestBeforePeakWorkingSetBytes =
                current.LargestRequestBeforePeakWorkingSetBytes,
            LargestRequestAfterCurrentWorkingSetBytes =
                current.LargestRequestAfterCurrentWorkingSetBytes,
            LargestRequestAfterPeakWorkingSetBytes =
                current.LargestRequestAfterPeakWorkingSetBytes,
            ExternalEgressCanaryBlocked =
                current.ExternalEgressCanaryBlocked,
            MarkItDownReachable = current.MarkItDownReachable,
            MarkItDownReachabilityProbe =
                current.MarkItDownReachabilityProbe,
            EgressEnforcement = current.EgressEnforcement,
            EgressResidualLimitation =
                current.EgressResidualLimitation,
            LastRecommendationSourceItemId =
                current.LastRecommendationSourceItemId,
            LastRecommendationResultsJson =
                current.LastRecommendationResultsJson,
            LastRecommendationEffectiveFloor =
                current.LastRecommendationEffectiveFloor,
        };
    }
}

internal sealed record PluginSettings(
    bool Enabled,
    string ModelDirectory,
    string SnapshotPath,
    string MarkItDownEndpoint,
    int SearchLimit)
{
    internal PluginConfiguration ToConfiguration() => new()
    {
        Enabled = Enabled,
        ModelDirectory = ModelDirectory,
        SnapshotPath = SnapshotPath,
        MarkItDownEndpoint = MarkItDownEndpoint,
        SearchLimit = SearchLimit,
    };
}
