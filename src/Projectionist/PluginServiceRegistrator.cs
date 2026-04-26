using Jellyfin.Plugin.Projectionist.Providers;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Projectionist;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost serverApplicationHost)
    {
        services.AddSingleton<PrerollDiscoveryService>();
        services.AddSingleton<PrerollSelector>();
        services.AddSingleton<SessionTracker>();
        services.AddSingleton<IIntroProvider, PrerollIntroProvider>();
    }
}
