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

    /// <summary>
    /// Gets or sets a value indicating whether albums get a Jellyfin tag from their files' RELEASECATEGORY tag
    /// (e.g. "Official Soundtrack", "Game Rip") after each library scan.
    /// </summary>
    public bool EnableCategoryTags { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether albums get a Jellyfin tag from their files' SERIES (or FRANCHISE) tag
    /// after each library scan.
    /// </summary>
    public bool EnableSeriesTags { get; set; } = true;

    /// <summary>
    /// Reads an album tag field's on/off setting by property name.
    /// </summary>
    /// <param name="setting">The property name, e.g. <c>nameof(EnablePlatformTags)</c>.</param>
    /// <returns>The setting; <c>false</c> for an unknown name.</returns>
    public bool IsEnabled(string setting) => setting switch
    {
        nameof(EnablePlatformTags) => EnablePlatformTags,
        nameof(EnableCategoryTags) => EnableCategoryTags,
        nameof(EnableSeriesTags) => EnableSeriesTags,
        _ => false
    };
}
