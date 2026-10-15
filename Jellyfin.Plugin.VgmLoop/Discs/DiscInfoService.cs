using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VgmLoop.Discs;

/// <summary>
/// Reads disc names from audio tags with ATL (the tag library Jellyfin itself uses, so FLAC, Ogg, MP3, M4A and the
/// rest are covered), caching by path + size + mtime.
/// </summary>
public class DiscInfoService
{
    private const int MaxEntries = 8192;

    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<DiscInfoService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscInfoService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public DiscInfoService(ILogger<DiscInfoService> logger)
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
    public string? GetDiscName(string path)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{path}|{file.Length}-{file.LastWriteTimeUtc.Ticks}");
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? name = null;
        try
        {
            var track = new ATL.Track(path);
            name = DiscNames.FromFields(track.AdditionalFields);
        }
#pragma warning disable CA1031 // A tag we can't read just means no disc name; never fail the request over it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "VGM Loop: could not read tags of {Path}", path);
        }

        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        _cache[key] = name;
        return name;
    }
}
