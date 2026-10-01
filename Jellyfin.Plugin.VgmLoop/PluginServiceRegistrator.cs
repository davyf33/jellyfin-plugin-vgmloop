using Jellyfin.Plugin.VgmLoop.Loop;
using Jellyfin.Plugin.VgmLoop.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VgmLoop;

/// <summary>
/// Registers plugin services with the server's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddTransient<IStartupFilter, WebInjectionStartupFilter>();
        serviceCollection.AddSingleton<LoopInfoService>();
    }
}
