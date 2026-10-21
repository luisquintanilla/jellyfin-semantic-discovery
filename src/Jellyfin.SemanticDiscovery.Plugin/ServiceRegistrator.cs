using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);

        NativeOnnxRuntimeResolver.Install();
        serviceCollection.AddSingleton<SemanticDiscoveryPaths>();
        serviceCollection.AddSingleton<JellyfinIndexSource>();
        serviceCollection.AddSingleton<JellyfinMediaAccessPolicy>();
        serviceCollection.AddSingleton<SemanticDiscoveryRuntime>();
        serviceCollection.AddHostedService<OnnxStartupSmokeService>();
    }
}
