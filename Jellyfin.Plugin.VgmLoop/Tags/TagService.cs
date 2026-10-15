using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.VgmLoop.Discs;
using Jellyfin.Plugin.VgmLoop.Platforms;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VgmLoop.Tags;

/// <summary>
/// What the plugin uses from one file's tags.
/// </summary>
/// <param name="DiscName">Disc name (subtitle), or <c>null</c>.</param>
/// <param name="Platforms">Platforms, possibly empty.</param>
public sealed record FileTags(string? DiscName, IReadOnlyList<string> Platforms)
{
    /// <summary>Gets the value for a file without usable tags.</summary>
    public static FileTags Empty { get; } = new(null, []);
}

/// <summary>
/// Reads disc names and platforms from audio tags with ATL (the tag library Jellyfin itself uses, so FLAC, Ogg, MP3,
/// M4A and the rest are covered), caching by path + size + mtime.
/// </summary>
public class TagService
{
    private const int MaxEntries = 8192;

    private readonly ConcurrentDictionary<string, FileTags> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<TagService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public TagService(ILogger<TagService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the named discs of an album.
    /// </summary>
    /// <param name="tracks">The album's tracks.</param>
    /// <returns>Named discs, by disc number.</returns>
    public IReadOnlyList<DiscName> GetDiscs(IEnumerable<DiscTrack> tracks)
        => DiscNames.Build(tracks, t => GetDiscName(t.Path));

    /// <summary>
    /// Gets the disc name tagged in one file.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The name, or <c>null</c> when the file has none or can't be read.</returns>
    public string? GetDiscName(string path) => Get(path).DiscName;

    /// <summary>
    /// Gets the platforms tagged in one file.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The platforms; empty when the file has none or can't be read.</returns>
    public IReadOnlyList<string> GetPlatforms(string path) => Get(path).Platforms;

    /// <summary>
    /// Gets the tags the plugin uses from one file.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The tags; <see cref="FileTags.Empty"/> when the file can't be read.</returns>
    public FileTags Get(string path)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return FileTags.Empty;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return FileTags.Empty;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{path}|{file.Length}-{file.LastWriteTimeUtc.Ticks}");
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var tags = FileTags.Empty;
        try
        {
            var fields = new ATL.Track(path).AdditionalFields;
            tags = new FileTags(DiscNames.FromFields(fields), PlatformTags.FromFields(fields));
        }
#pragma warning disable CA1031 // A tag we can't read just means no value; never fail the caller over it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "VGM Loop: could not read tags of {Path}", path);
        }

        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        _cache[key] = tags;
        return tags;
    }
}
