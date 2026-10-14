using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VgmLoop.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the VGM Loop player is injected into jellyfin-web.
    /// </summary>
    public bool EnablePlayer { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether disc names from tags are shown next to "Disc N" on album pages.
    /// </summary>
    public bool EnableDiscNames { get; set; } = true;
}
