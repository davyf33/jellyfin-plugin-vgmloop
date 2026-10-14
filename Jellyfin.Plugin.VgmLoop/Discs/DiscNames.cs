using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.VgmLoop.Discs;

/// <summary>
/// One audio track as far as disc naming is concerned.
/// </summary>
/// <param name="Id">Item id.</param>
/// <param name="DiscNumber">Disc number (Jellyfin's <c>ParentIndexNumber</c>).</param>
/// <param name="TrackNumber">Track number (Jellyfin's <c>IndexNumber</c>).</param>
/// <param name="Path">File path.</param>
public sealed record DiscTrack(Guid Id, int? DiscNumber, int? TrackNumber, string Path);

/// <summary>
/// A named disc of an album.
/// </summary>
/// <param name="DiscNumber">Disc number.</param>
/// <param name="Name">Disc name (subtitle).</param>
/// <param name="ItemIds">Every track on the disc, in album order.</param>
public sealed record DiscName(int DiscNumber, string Name, IReadOnlyList<Guid> ItemIds);

/// <summary>
/// Pure disc-name logic: which tag holds the name, and how an album's tracks become named discs.
/// </summary>
public static class DiscNames
{
    /// <summary>
    /// Tag keys that hold a disc name, in order of preference, matched case-insensitively against what ATL reports:
    /// <c>DISCSUBTITLE</c> (Vorbis comments, MP4 freeform and ID3 TXXX as written by MusicBrainz Picard), <c>TSST</c>
    /// (ID3v2.4 "set subtitle"), and spellings seen in the wild.
    /// </summary>
    public static readonly string[] TagKeys = ["DISCSUBTITLE", "TSST", "SETSUBTITLE", "DISC_SUBTITLE", "DISCNAME", "DISC_NAME", "DISCTITLE", "DISC_TITLE"];

    /// <summary>How many tracks of a disc are read before giving up on a name (normally the first track has it).</summary>
    public const int MaxTracksReadPerDisc = 3;

    /// <summary>Longest name returned; longer values are cut.</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// Picks the disc name from a track's tag fields.
    /// </summary>
    /// <param name="fields">Tag fields (key → value), any key casing.</param>
    /// <returns>The trimmed name, or <c>null</c> when there is none.</returns>
    public static string? FromFields(IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            if (key is not null)
            {
                byKey.TryAdd(key.Trim(), value);
            }
        }

        foreach (var key in TagKeys)
        {
            if (byKey.TryGetValue(key, out var value) && Clean(value) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Groups an album's tracks by disc and names each disc from the first of its tracks that carries a name.
    /// Tracks without a disc number are ignored (the web client shows no "Disc N" header for them).
    /// </summary>
    /// <param name="tracks">The album's tracks, any order.</param>
    /// <param name="readName">Reads a track's disc name; may return <c>null</c>.</param>
    /// <returns>Named discs, by disc number. Discs without a name are left out.</returns>
    public static IReadOnlyList<DiscName> Build(IEnumerable<DiscTrack> tracks, Func<DiscTrack, string?> readName)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(readName);
        var result = new List<DiscName>();
        var discs = tracks
            .Where(t => t.DiscNumber is not null)
            .GroupBy(t => t.DiscNumber!.Value)
            .OrderBy(g => g.Key);
        foreach (var disc in discs)
        {
            var ordered = disc.OrderBy(t => t.TrackNumber ?? int.MaxValue).ThenBy(t => t.Path, StringComparer.Ordinal).ToList();
            var name = ordered.Take(MaxTracksReadPerDisc).Select(readName).FirstOrDefault(n => n is not null);
            if (name is not null)
            {
                result.Add(new DiscName(disc.Key, name, ordered.Select(t => t.Id).ToList()));
            }
        }

        return result;
    }

    /// <summary>
    /// Trims a raw tag value, keeps the first of several values (Jellyfin has ATL join them with U+001F), drops
    /// anything after a NUL, and caps the length.
    /// </summary>
    /// <param name="value">Raw value.</param>
    /// <returns>The cleaned value, or <c>null</c> when empty.</returns>
    public static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var nul = value.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
        {
            value = value[..nul];
        }

        var first = value.Split('\u001F').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
        if (first is null)
        {
            return null;
        }

        return first.Length > MaxNameLength ? first[..MaxNameLength].TrimEnd() : first;
    }
}
