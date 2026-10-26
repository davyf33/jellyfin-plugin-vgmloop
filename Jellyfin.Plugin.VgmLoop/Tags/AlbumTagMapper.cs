using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.VgmLoop.Tags;

/// <summary>
/// One field's input to <see cref="AlbumTagMapper.Plan"/>.
/// </summary>
/// <param name="Field">The field.</param>
/// <param name="Enabled">Whether its setting is on. A disabled field's tags and record are left as they are.</param>
/// <param name="Values">What the album's files say now.</param>
public sealed record AlbumTagInput(AlbumTagField Field, bool Enabled, IReadOnlyList<string> Values);

/// <summary>
/// What to change on an album.
/// </summary>
/// <param name="Changed">Whether anything changes.</param>
/// <param name="Tags">The album's new tags.</param>
/// <param name="ProviderIds">Provider id updates: a value to set, or <c>null</c> to remove the key.</param>
public sealed record AlbumTagPlan(bool Changed, string[] Tags, IReadOnlyDictionary<string, string?> ProviderIds);

/// <summary>
/// Pure album-tag logic shared by every <see cref="AlbumTagField"/>: reading values from file tags, and working out an
/// album's new Jellyfin tags. Each field records the tags it added in its own provider id, so it only ever removes
/// those, never another field's tags or a tag the user added.
/// </summary>
public static class AlbumTagMapper
{
    /// <summary>Separator inside a provider id value, and one of the value separators in file tags.</summary>
    public const char Separator = '|';

    /// <summary>Most values taken from one field of one file.</summary>
    public const int MaxValues = 5;

    /// <summary>Longest value; longer ones are cut.</summary>
    public const int MaxLength = 100;

    /// <summary>How many tracks of an album are read before giving up on a field.</summary>
    public const int MaxTracksRead = 3;

    /// <summary>
    /// Reads every field's values from a track's tag fields.
    /// </summary>
    /// <param name="fields">Tag fields (key → value), any key casing.</param>
    /// <returns>Values by <see cref="AlbumTagField.ProviderId"/>; fields without values are left out.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadAll(IEnumerable<KeyValuePair<string, string>> fields)
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

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var field in AlbumTagFields.All)
        {
            var values = Read(byKey, field);
            if (values.Count > 0)
            {
                result[field.ProviderId] = values;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads one field's values. Several values (multi-value tags, or separated by <c>;</c> or <c>|</c>) all count.
    /// </summary>
    /// <param name="fields">Tag fields (key → value), any key casing.</param>
    /// <param name="field">The field.</param>
    /// <returns>The values, trimmed and de-duplicated; empty when there are none.</returns>
    public static IReadOnlyList<string> Read(IEnumerable<KeyValuePair<string, string>> fields, AlbumTagField field)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(field);
        var byKey = fields as IReadOnlyDictionary<string, string> ?? fields
            .Where(kv => kv.Key is not null)
            .GroupBy(kv => kv.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in field.Keys)
        {
            if (TryGet(byKey, key, out var value) && Split(value) is { Count: > 0 } values)
            {
                return values;
            }
        }

        return [];
    }

    /// <summary>
    /// Works out an album's new tags and records.
    /// </summary>
    /// <param name="tagsLocked">Whether the album's Tags field is locked; nothing changes then.</param>
    /// <param name="tags">The album's current tags.</param>
    /// <param name="providerIds">The album's provider ids (each field's record of what it added).</param>
    /// <param name="inputs">One entry per field.</param>
    /// <returns>The plan.</returns>
    public static AlbumTagPlan Plan(bool tagsLocked, IReadOnlyList<string> tags, IReadOnlyDictionary<string, string> providerIds, IReadOnlyList<AlbumTagInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(providerIds);
        ArgumentNullException.ThrowIfNull(inputs);
        var noChange = new AlbumTagPlan(false, tags.ToArray(), new Dictionary<string, string?>());
        if (tagsLocked)
        {
            return noChange;
        }

        var cmp = StringComparer.OrdinalIgnoreCase;
        var applied = inputs.ToDictionary(i => i.Field.ProviderId, i => ParseApplied(providerIds.GetValueOrDefault(i.Field.ProviderId)));

        // Tags some field owns after this run: what enabled fields say now, plus whatever disabled fields added before.
        var stillWanted = new HashSet<string>(cmp);
        foreach (var input in inputs)
        {
            stillWanted.UnionWith(input.Enabled ? input.Values : applied[input.Field.ProviderId]);
        }

        // Drop a tag only if a field added it, it no longer says it, and no other field wants it.
        var stale = new HashSet<string>(cmp);
        foreach (var input in inputs.Where(i => i.Enabled))
        {
            stale.UnionWith(applied[input.Field.ProviderId].Where(a => !input.Values.Contains(a, cmp)));
        }

        stale.ExceptWith(stillWanted);
        var ownedBefore = new HashSet<string>(applied.Values.SelectMany(v => v), cmp);
        var result = tags.Where(t => !stale.Contains(t)).ToList();
        var addedNow = new HashSet<string>(cmp);
        var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var input in inputs.Where(i => i.Enabled))
        {
            var mine = applied[input.Field.ProviderId];
            var record = new List<string>();
            foreach (var value in input.Values)
            {
                if (!result.Contains(value, cmp))
                {
                    result.Add(value);
                    addedNow.Add(value);
                    record.Add(value);
                }
                else if (mine.Contains(value, cmp) || ownedBefore.Contains(value) || addedNow.Contains(value))
                {
                    // Already ours, or another field's: share it, so it stays until no field wants it.
                    record.Add(value);
                }

                // Otherwise the user already had this tag: leave it theirs and don't record it.
            }

            var formatted = FormatApplied(record);
            if (!string.Equals(formatted, providerIds.GetValueOrDefault(input.Field.ProviderId), StringComparison.Ordinal))
            {
                updates[input.Field.ProviderId] = formatted;
            }
        }

        var changed = updates.Count > 0 || !result.SequenceEqual(tags, StringComparer.Ordinal);
        return changed ? new AlbumTagPlan(true, result.ToArray(), updates) : noChange;
    }

    /// <summary>
    /// Reads a field's record of the tags it added.
    /// </summary>
    /// <param name="value">Stored provider id value, or <c>null</c>.</param>
    /// <returns>The tags.</returns>
    public static IReadOnlyList<string> ParseApplied(string? value)
        => string.IsNullOrEmpty(value) ? [] : value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Builds a field's record of the tags it added.
    /// </summary>
    /// <param name="values">The tags.</param>
    /// <returns>The value, or <c>null</c> when there are none (the key is then removed).</returns>
    public static string? FormatApplied(IReadOnlyList<string> values)
        => values is { Count: > 0 } ? string.Join(Separator, values) : null;

    private static bool TryGet(IReadOnlyDictionary<string, string> byKey, string key, out string? value)
    {
        if (byKey.TryGetValue(key, out value))
        {
            return true;
        }

        // A caller's dictionary may be case-sensitive.
        foreach (var (k, v) in byKey)
        {
            if (string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        return false;
    }

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
            .Take(MaxValues)
            .ToList();
    }
}
