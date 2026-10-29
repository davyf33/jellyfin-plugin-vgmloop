using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.VgmLoop.Configuration;
using Jellyfin.Plugin.VgmLoop.Loop;

namespace Jellyfin.Plugin.VgmLoop.Api;

/// <summary>
/// Response of <c>GET /VgmLoop/Items/{itemId}/LoopInfo</c>. Property names are fixed camelCase regardless of
/// Jellyfin's JSON profile, because player.js reads them directly.
/// </summary>
public sealed class LoopInfoDto
{
    /// <summary>Gets or sets the item id (N format).</summary>
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the track has a valid, playable loop.</summary>
    [JsonPropertyName("hasLoop")]
    public bool HasLoop { get; set; }

    /// <summary>Gets or sets the codec.</summary>
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "unknown";

    /// <summary>Gets or sets the container.</summary>
    [JsonPropertyName("container")]
    public string Container { get; set; } = "unknown";

    /// <summary>Gets or sets the native decode sample rate.</summary>
    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    [JsonPropertyName("channels")]
    public int Channels { get; set; }

    /// <summary>Gets or sets the total samples at <see cref="SampleRate"/>.</summary>
    [JsonPropertyName("totalSamples")]
    public long? TotalSamples { get; set; }

    /// <summary>Gets or sets the Opus pre-skip.</summary>
    [JsonPropertyName("preSkip")]
    public int? PreSkip { get; set; }

    /// <summary>Gets or sets the number of bytes to drop before decoding (leading ID3v2).</summary>
    [JsonPropertyName("audioDataOffset")]
    public long AudioDataOffset { get; set; }

    /// <summary>Gets or sets the loop start in samples.</summary>
    [JsonPropertyName("loopStart")]
    public long? LoopStart { get; set; }

    /// <summary>Gets or sets the exclusive loop end in samples.</summary>
    [JsonPropertyName("loopEnd")]
    public long? LoopEnd { get; set; }

    /// <summary>Gets or sets how the loop end was derived.</summary>
    [JsonPropertyName("convention")]
    public string? Convention { get; set; }

    /// <summary>Gets or sets the loop tags as found.</summary>
    [JsonPropertyName("rawTags")]
    public IReadOnlyDictionary<string, string> RawTags { get; set; } = new Dictionary<string, string>();

    /// <summary>Gets or sets why there is no loop.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    /// <summary>Gets or sets how many loop passes play-through plays before fading; absent when play-through is off.</summary>
    [JsonPropertyName("playthroughLoops")]
    public int? PlaythroughLoops { get; set; }

    /// <summary>Gets or sets the play-through fade length in seconds; absent when play-through is off.</summary>
    [JsonPropertyName("playthroughFadeSeconds")]
    public double? PlaythroughFadeSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether play-through applies to tracks with an outro too; absent when off.</summary>
    [JsonPropertyName("playthroughAllLoops")]
    public bool? PlaythroughAllLoops { get; set; }

    /// <summary>Gets or sets the loop output for iPhone and iPad (<c>stream</c>); absent for the default (direct).</summary>
    [JsonPropertyName("iosOutput")]
    public string? IosOutput { get; set; }

    /// <summary>Gets or sets the size-mtime version string.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Builds the DTO.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="info">File loop info.</param>
    /// <param name="config">Plugin settings (play-through), or <c>null</c> for the defaults.</param>
    /// <returns>The DTO.</returns>
    public static LoopInfoDto From(Guid itemId, FileLoopInfo info, PluginConfiguration? config = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        config ??= new PluginConfiguration();
        var h = info.Header;
        var l = info.Loop;
        return new LoopInfoDto
        {
            ItemId = itemId.ToString("N"),
            HasLoop = l.HasLoop,
            Codec = h.Codec,
            Container = h.Container,
            SampleRate = h.SampleRate,
            Channels = h.Channels,
            TotalSamples = h.TotalSamples,
            PreSkip = h.PreSkip,
            AudioDataOffset = h.AudioDataOffset,
            LoopStart = l.LoopStart,
            LoopEnd = l.LoopEnd,
            Convention = l.Convention,
            RawTags = l.RawTags,
            Reason = l.Reason,
            PlaythroughLoops = config.EnablePlaythrough ? Math.Clamp(config.PlaythroughLoops, 1, 5) : null,
            PlaythroughFadeSeconds = config.EnablePlaythrough ? Math.Clamp(config.PlaythroughFadeSeconds, 0, 30) : null,
            PlaythroughAllLoops = config.EnablePlaythrough && config.PlaythroughAllLoops ? true : null,
            IosOutput = string.Equals(config.IosLoopOutput, "stream", StringComparison.OrdinalIgnoreCase) ? "stream" : null,
            FileSize = info.FileSize,
            Version = info.Version
        };
    }
}
