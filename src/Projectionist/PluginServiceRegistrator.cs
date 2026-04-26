using Jellyfin.Plugin.Projectionist.Providers;
using Jellyfin.Plugin.Projectionist.Services;
using Jellyfin.Plugin.Projectionist.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Projectionist;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost serverApplicationHost)
    {
        services.AddSingleton<PrerollDiscoveryService>();
        services.AddSingleton<CooldownStore>();
        services.AddSingleton<StatsStore>();
        services.AddSingleton<SessionTracker>();
        services.AddSingleton<HiddenLibraryManager>();
        services.AddSingleton<SeriesPrerollFinder>();
        services.AddSingleton<TrailerFetcher>();
        services.AddSingleton<PrerollSelector>(sp =>
            new PrerollSelector(sp.GetService<CooldownStore>()));
        services.AddSingleton<IIntroProvider, PrerollIntroProvider>();
        services.AddSingleton<IStartupFilter, IndexHtmlInjectionFilter>();
        services.AddHostedService<WebInjector>();
    }
}
