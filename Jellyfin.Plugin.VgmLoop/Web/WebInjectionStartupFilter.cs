using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.VgmLoop.Web;

/// <summary>
/// Puts <see cref="WebInjectionMiddleware"/> in front of Jellyfin's own pipeline (before static files).
/// </summary>
public class WebInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<WebInjectionMiddleware>();
            next(app);
        };
    }
}
