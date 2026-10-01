using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VgmLoop.Loop;

/// <summary>
/// Result of resolving loop tags against a track.
/// </summary>
public sealed class LoopResolution
{
    /// <summary>Gets a value indicating whether the track has a valid loop.</summary>
    public bool HasLoop { get; init; }

    /// <summary>Gets the loop start, in native-rate samples.</summary>
    public long? LoopStart { get; init; }

    /// <summary>Gets the loop end (exclusive), in native-rate samples.</summary>
    public long? LoopEnd { get; init; }

    /// <summary>Gets how the end was derived.</summary>
    public string? Convention { get; init; }

    /// <summary>Gets why there is no loop, when <see cref="HasLoop"/> is false.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the loop-related tags exactly as found.</summary>
    public IReadOnlyDictionary<string, string> RawTags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Loop tag resolution (see docs/design.md). Port of <c>resolveLoop</c>/<c>parseLoopVal</c> from <c>tools/loop-seam-probe.html</c>.
/// </summary>
public static partial class LoopResolver
{
    private static readonly string[] StartKeys = ["LOOPSTART", "LOOP_START"];
    private static readonly string[] LengthKeys = ["LOOPLENGTH", "LOOP_LENGTH"];
    private static readonly string[] EndKeys = ["LOOPEND", "LOOP_END"];

    /// <summary>
    /// Parses a loop tag value into native-rate samples.
    /// Accepts integer samples, decimal seconds (<c>12.5</c>, <c>.5</c>), <c>m:ss(.fff)</c> and <c>h:mm:ss(.fff)</c>.
    /// </summary>
    /// <param name="value">Raw tag value.</param>
    /// <param name="rate">Native sample rate.</param>
    /// <returns>Samples, or <c>null</c> when the value can't be parsed.</returns>
    public static long? ParseValue(string? value, int rate)
    {
        if (value is null)
        {
            return null;
        }

        var s = value.Trim();
        if (SamplesRegex().IsMatch(s))
        {
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }

        if (SecondsRegex().IsMatch(s))
        {
            return ToSamples(Num(s), rate);
        }

        var m = MinSecRegex().Match(s);
        if (m.Success)
        {
            return ToSamples((Num(m.Groups[1].Value) * 60) + Num(m.Groups[2].Value), rate);
        }

        m = HourMinSecRegex().Match(s);
        if (m.Success)
        {
            return ToSamples((Num(m.Groups[1].Value) * 3600) + (Num(m.Groups[2].Value) * 60) + Num(m.Groups[3].Value), rate);
        }

        return null;
    }

    /// <summary>
    /// Resolves loop points from tags.
    /// </summary>
    /// <param name="tags">Vorbis comments (upper-cased keys).</param>
    /// <param name="rate">Native sample rate.</param>
    /// <param name="totalSamples">Total samples, if known.</param>
    /// <returns>The resolution.</returns>
    public static LoopResolution Resolve(IReadOnlyDictionary<string, string> tags, int rate, long? totalSamples)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        var st = Pick(tags, StartKeys, raw);
        var ln = Pick(tags, LengthKeys, raw);
        var en = Pick(tags, EndKeys, raw);

        LoopResolution NoLoop(string reason, long? s = null, long? e = null, string? convention = null)
            => new() { HasLoop = false, Reason = reason, LoopStart = s, LoopEnd = e, Convention = convention, RawTags = raw };

        if (st is null)
        {
            return NoLoop("No loop start tag.");
        }

        if (rate <= 0)
        {
            return NoLoop("Unknown sample rate.");
        }

        var start = ParseValue(st, rate);
        if (start is null)
        {
            return NoLoop("Loop start value could not be parsed.");
        }

        var length = ParseValue(ln, rate);
        var end = ParseValue(en, rate);
        long? endEx;
        string convention;
        if (length is not null)
        {
            endEx = start + length;
            if (end is null)
            {
                convention = "start + length";
            }
            else if (end == endEx)
            {
                convention = "LOOPEND exclusive (equals start + length)";
            }
            else if (end == endEx - 1)
            {
                convention = "LOOPEND inclusive (equals start + length - 1)";
            }
            else
            {
                convention = "LOOPEND disagrees with start + length; using start + length";
            }
        }
        else if (end is not null)
        {
            endEx = end;
            convention = totalSamples is not null && end == totalSamples - 1
                ? "LOOPEND only, equals the last sample index: probably inclusive, treated as exclusive"
                : "LOOPEND only, treated as exclusive";
        }
        else
        {
            endEx = totalSamples;
            convention = "no end tag: loops at the end of the track";
        }

        if (totalSamples is null || endEx is null)
        {
            return NoLoop("Track length is unknown.", start, endEx, convention);
        }

        if (start < 0 || start >= endEx || endEx > totalSamples)
        {
            return NoLoop(
                string.Create(CultureInfo.InvariantCulture, $"Loop range {start}..{endEx} is outside 0..{totalSamples}."),
                start,
                endEx,
                convention);
        }

        if ((endEx.Value - start.Value) * 10 < rate)
        {
            return NoLoop("Loop is shorter than 0.1 s.", start, endEx, convention);
        }

        return new LoopResolution { HasLoop = true, LoopStart = start, LoopEnd = endEx, Convention = convention, RawTags = raw };
    }

    private static string? Pick(IReadOnlyDictionary<string, string> tags, string[] keys, Dictionary<string, string> found)
    {
        foreach (var key in keys)
        {
            if (tags.TryGetValue(key, out var value))
            {
                found[key] = value;
                return value;
            }
        }

        return null;
    }

    private static double Num(string s) => double.Parse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    // Same rounding as JavaScript's Math.round for non-negative values.
    private static long? ToSamples(double seconds, int rate)
    {
        var v = Math.Floor((seconds * rate) + 0.5);
        return v is >= 0 and < long.MaxValue ? (long)v : null;
    }

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SamplesRegex();

    [GeneratedRegex(@"^[0-9]*\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SecondsRegex();

    [GeneratedRegex(@"^([0-9]+):([0-9]+(?:\.[0-9]+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex MinSecRegex();

    [GeneratedRegex(@"^([0-9]+):([0-9]+):([0-9]+(?:\.[0-9]+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex HourMinSecRegex();
}
