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

    /// <summary>
    /// Gets or sets a value indicating whether tracks with loop tags get a loop icon in album and playlist track lists.
    /// </summary>
    public bool EnableLoopIcons { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether albums get a Jellyfin tag per platform from their files' PLATFORM tag
    /// after each library scan.
    /// </summary>
    public bool EnablePlatformTags { get; set; } = true;
}
