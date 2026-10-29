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
    /// Gets or sets a value indicating whether cut rips (no audio after the loop end) play their loop a few times and
    /// fade out when Repeat One is off, instead of stopping dead at the loop end.
    /// </summary>
    public bool EnablePlaythrough { get; set; } = true;

    /// <summary>Gets or sets how many times play-through plays the loop before fading (1-5).</summary>
    public int PlaythroughLoops { get; set; } = 2;

    /// <summary>Gets or sets the play-through fade length in seconds (0-30).</summary>
    public double PlaythroughFadeSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether play-through applies to every loop track, not only cut rips: tracks with
    /// an outro after the loop also loop N times and fade instead of playing their outro.
    /// </summary>
    public bool PlaythroughAllLoops { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether album pages get Play and Shuffle buttons on each "Disc N" header.
    /// </summary>
    public bool EnableDiscButtons { get; set; } = true;

    /// <summary>
    /// Gets or sets how loop tracks reach the speakers on iPhone and iPad: <c>direct</c> (Web Audio straight to the
    /// output) or <c>stream</c> (through a hidden audio element, which iOS treats as playing media for the lock screen
    /// and Control Center). A browser's own <c>?vgmloop=stream</c> / <c>direct</c> switch overrides it.
    /// </summary>
    public string IosLoopOutput { get; set; } = "direct";

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
