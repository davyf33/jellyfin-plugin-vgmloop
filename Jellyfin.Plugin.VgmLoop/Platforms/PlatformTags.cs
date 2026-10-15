using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.VgmLoop.Platforms;

/// <summary>
/// Pure platform-tag logic: which file tags hold the platform, and how it becomes Jellyfin tags on an album.
/// </summary>
public static class PlatformTags
{
    /// <summary>
    /// File tag keys that hold the platform, in order of preference, matched case-insensitively against what ATL
    /// reports (Vorbis comments, ID3 <c>TXXX</c>, MP4 freeform).
    /// </summary>
    public static readonly string[] TagKeys = ["PLATFORM", "CONSOLE", "SYSTEM"];

    /// <summary>
    /// Provider id key on an album that remembers which Jellyfin tags this plugin added, so they can be replaced
    /// when the files change. Values are joined with <see cref="Separator"/>.
    /// </summary>
    public const string AppliedKey = "VgmLoopPlatform";

    /// <summary>Separator for <see cref="AppliedKey"/> values.</summary>
    public const char Separator = '|';

    /// <summary>Most platforms taken from one file.</summary>
    public const int MaxPlatforms = 5;

    /// <summary>Longest platform name; longer values are cut.</summary>
    public const int MaxLength = 100;

    /// <summary>How many tracks of an album are read before giving up on a platform.</summary>
    public const int MaxTracksRead = 3;

    /// <summary>
    /// Picks the platforms from a track's tag fields. Several values (multi-value tags, or <c>;</c>-separated) all
    /// count, e.g. a game released on two consoles.
    /// </summary>
    /// <param name="fields">Tag fields (key → value), any key casing.</param>
    /// <returns>The platforms, trimmed and de-duplicated; empty when there are none.</returns>
    public static IReadOnlyList<string> FromFields(IEnumerable<KeyValuePair<string, string>> fields)
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
            if (byKey.TryGetValue(key, out var value) && Split(value) is { Count: > 0 } platforms)
            {
                return platforms;
            }
        }

        return [];
    }

    /// <summary>
    /// Works out an album's new Jellyfin tags: drops the platform tags this plugin added before that no longer apply,
    /// adds the current ones, and keeps every other tag as it is.
    /// </summary>
    /// <param name="tags">The album's current tags.</param>
    /// <param name="applied">The platform tags this plugin added last time (from <see cref="AppliedKey"/>).</param>
    /// <param name="platforms">The platforms the files say now.</param>
    /// <returns>The new tags and whether they differ from <paramref name="tags"/>.</returns>
    public static (string[] Tags, bool Changed) Apply(IReadOnlyList<string> tags, IReadOnlyList<string> applied, IReadOnlyList<string> platforms)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(platforms);
        var stale = applied.Where(a => !platforms.Contains(a, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = tags.Where(t => !stale.Contains(t)).ToList();
        foreach (var p in platforms)
        {
            if (!result.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(p);
            }
        }

        var changed = result.Count != tags.Count || !result.SequenceEqual(tags, StringComparer.Ordinal);
        return (result.ToArray(), changed);
    }

    /// <summary>
    /// Reads the <see cref="AppliedKey"/> value.
    /// </summary>
    /// <param name="value">Stored value, or <c>null</c>.</param>
    /// <returns>The platform tags added last time.</returns>
    public static IReadOnlyList<string> ParseApplied(string? value)
        => string.IsNullOrEmpty(value) ? [] : value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Builds the <see cref="AppliedKey"/> value.
    /// </summary>
    /// <param name="platforms">Platforms now applied.</param>
    /// <returns>The value, or <c>null</c> when there are none (the key is then removed).</returns>
    public static string? FormatApplied(IReadOnlyList<string> platforms)
        => platforms is { Count: > 0 } ? string.Join(Separator, platforms) : null;

    private static List<string> Split(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var nul = value.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
        {
            value = value[..nul];
        }

        return value
            .Split(['\u001F', ';', Separator], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Length > MaxLength ? p[..MaxLength].TrimEnd() : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPlatforms)
            .ToList();
    }
}
