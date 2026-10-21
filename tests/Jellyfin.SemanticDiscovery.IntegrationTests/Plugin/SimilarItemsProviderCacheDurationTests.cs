using System.Reflection;
using Jellyfin.SemanticDiscovery.Plugin;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Plugin;

/// <summary>
/// Confirms the ineffective, hard-coded 5-minute <c>CacheDuration</c>
/// overrides were removed from the local Movie/Series/Book similar-items
/// providers, so they fall through to <see cref="ISimilarItemsProvider"/>'s
/// own default (<see langword="null"/> — meaning results are not cached at
/// this layer, matching the runtime's own semantic-index-generation cache).
/// </summary>
public sealed class SimilarItemsProviderCacheDurationTests
{
    [Fact]
    public void InterfaceDefaultCacheDurationIsNull()
    {
        // If ISimilarItemsProvider.CacheDuration had no default interface
        // implementation, this minimal pass-through class would fail to
        // compile without declaring the member itself.
        ISimilarItemsProvider minimal = new MinimalProvider();

        Assert.Null(minimal.CacheDuration);
    }

    [Theory]
    [InlineData(typeof(MovieSimilarItemsProvider))]
    [InlineData(typeof(SeriesSimilarItemsProvider))]
    [InlineData(typeof(BookSimilarItemsProvider))]
    public void LocalSimilarItemsProvidersDoNotDeclareTheirOwnCacheDuration(Type providerType)
    {
        PropertyInfo? declared = providerType.GetProperty(
            nameof(ISimilarItemsProvider.CacheDuration),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Null(declared);
        Assert.Contains(
            typeof(ISimilarItemsProvider),
            providerType.GetInterfaces());
    }

    private sealed class MinimalProvider : ISimilarItemsProvider
    {
        public string Name => "Minimal";

        public MetadataPluginType Type => MetadataPluginType.LocalSimilarityProvider;
    }
}
