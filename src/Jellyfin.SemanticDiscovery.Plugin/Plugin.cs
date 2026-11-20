using MediaBrowser.Common.Plugins;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class Plugin : BasePlugin
{
    public override string Name => "Semantic Discovery";

    public override string Description => "Semantic search and More Like This, backed by the semantic service.";

    public override Guid Id { get; } = Guid.Parse("de965148-5789-4e8c-9774-6d1409ef3478");
}
