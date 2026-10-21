using MediaBrowser.Model.Tasks;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class RebuildSemanticIndexTask(SemanticDiscoveryRuntime runtime) : IScheduledTask
{
    private readonly SemanticDiscoveryRuntime _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    public string Name => "Rebuild semantic discovery index";

    public string Key => "SemanticDiscoveryRebuild";

    public string Description =>
        "Incrementally rebuilds local metadata and full-content semantic indexes.";

    public string Category => "Semantic Discovery";

    public Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken) =>
        _runtime.RebuildAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger,
        },
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        },
    ];
}
