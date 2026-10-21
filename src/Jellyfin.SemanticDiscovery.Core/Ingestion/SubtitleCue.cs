namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

public sealed record SubtitleCue(long StartTicks, long EndTicks, string Text);
