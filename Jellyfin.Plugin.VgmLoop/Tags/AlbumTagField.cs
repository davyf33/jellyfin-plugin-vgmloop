using Jellyfin.Plugin.VgmLoop.Configuration;

namespace Jellyfin.Plugin.VgmLoop.Tags;

/// <summary>
/// A file tag that becomes Jellyfin tags on albums.
/// </summary>
/// <param name="Name">Short name for logs.</param>
/// <param name="Keys">File tag keys, in order of preference, matched case-insensitively.</param>
/// <param name="ProviderId">Album provider id key that remembers which Jellyfin tags this field added.</param>
/// <param name="Setting">Name of its on/off property in <see cref="PluginConfiguration"/> (see <see cref="PluginConfiguration.IsEnabled"/>).</param>
public sealed record AlbumTagField(string Name, string[] Keys, string ProviderId, string Setting);

/// <summary>
/// The file tags that become album tags. To add one, add a field here and a setting for it.
/// </summary>
public static class AlbumTagFields
{
    /// <summary>Platform, e.g. "Nintendo 3DS".</summary>
    public static readonly AlbumTagField Platform = new("platform", ["PLATFORM", "CONSOLE", "SYSTEM"], "VgmLoopPlatform", nameof(PluginConfiguration.EnablePlatformTags));

    /// <summary>Release category, e.g. "Official Soundtrack" or "Game Rip".</summary>
    public static readonly AlbumTagField Category = new("category", ["RELEASECATEGORY"], "VgmLoopCategory", nameof(PluginConfiguration.EnableCategoryTags));

    /// <summary>Series or franchise, e.g. "Animal Crossing".</summary>
    public static readonly AlbumTagField Series = new("series", ["SERIES", "FRANCHISE"], "VgmLoopSeries", nameof(PluginConfiguration.EnableSeriesTags));

    /// <summary>Gets every field, in the order their tags are added to an album.</summary>
    public static AlbumTagField[] All { get; } = [Platform, Category, Series];
}
