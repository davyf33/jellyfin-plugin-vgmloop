using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.VgmLoop.Loop;

/// <summary>
/// Stream parameters and Vorbis comments read from a FLAC or Ogg file header.
/// </summary>
public sealed class AudioHeader
{
    /// <summary>Gets the container: <c>flac</c>, <c>ogg</c> or <c>unknown</c>.</summary>
    public string Container { get; init; } = "unknown";

    /// <summary>Gets the codec: <c>flac</c>, <c>vorbis</c>, <c>opus</c> or <c>unknown</c>.</summary>
    public string Codec { get; init; } = "unknown";

    /// <summary>Gets the native decode sample rate (48000 for Opus).</summary>
    public int SampleRate { get; init; }

    /// <summary>Gets the channel count.</summary>
    public int Channels { get; init; }

    /// <summary>Gets the FLAC bits per sample.</summary>
    public int? BitsPerSample { get; init; }

    /// <summary>Gets the total decoded samples per channel at <see cref="SampleRate"/> (Opus: pre-skip removed).</summary>
    public long? TotalSamples { get; init; }

    /// <summary>Gets the Opus pre-skip in 48 kHz samples.</summary>
    public int? PreSkip { get; init; }

    /// <summary>Gets the Opus input sample rate (informational only).</summary>
    public int? InputSampleRate { get; init; }

    /// <summary>Gets the byte offset of the audio stream (after a leading ID3v2 tag), 0 when none.</summary>
    public long AudioDataOffset { get; init; }

    /// <summary>Gets the Vorbis comments, upper-cased keys, first occurrence wins.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets a description of what went wrong while parsing, if anything.</summary>
    public string? Error { get; init; }
}
