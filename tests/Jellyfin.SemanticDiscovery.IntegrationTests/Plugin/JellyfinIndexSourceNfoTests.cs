using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Plugin;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Plugin;

public sealed class JellyfinIndexSourceNfoTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"jellyfin-nfo-{Guid.NewGuid():N}");

    [Fact]
    public void ApplyNfoMetadataIndexesApprovedSidecarText()
    {
        Directory.CreateDirectory(_root);
        string mediaPath = Path.Combine(_root, "context-pipelines.flac");
        File.WriteAllBytes(mediaPath, []);
        File.WriteAllText(
            Path.Combine(_root, "context-pipelines.nfo"),
            """
            <musicvideo>
              <title>Context Pipelines</title>
              <plot>Structure-preserving extraction provenance.</plot>
              <genre>Technology</genre>
              <studio>.NET AI Stack demo</studio>
              <year>2026</year>
            </musicvideo>
            """);
        MetadataRecord metadata = new()
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Audio,
            ItemType = "Audio",
            MediaType = "Audio",
            Title = "Context Pipelines",
            Path = mediaPath,
        };

        MetadataRecord enriched =
            JellyfinIndexSource.ApplyNfoMetadata(metadata, mediaPath);

        Assert.Equal(
            "Structure-preserving extraction provenance.",
            enriched.Overview);
        Assert.Equal(2026, enriched.ProductionYear);
        Assert.Equal(["Technology"], enriched.Genres);
        Assert.Equal([".NET AI Stack demo"], enriched.Studios);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
