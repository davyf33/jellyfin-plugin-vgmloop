using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VgmLoop.Loop;

/// <summary>
/// Loop metadata for one file.
/// </summary>
/// <param name="Header">Parsed header.</param>
/// <param name="Loop">Resolved loop points.</param>
/// <param name="FileSize">File size in bytes.</param>
/// <param name="Version">Cache version string (<c>size-mtimeTicks</c>).</param>
public sealed record FileLoopInfo(AudioHeader Header, LoopResolution Loop, long FileSize, string Version);

/// <summary>
/// Reads and caches loop metadata, keyed by path + size + mtime.
/// </summary>
public class LoopInfoService
{
    /// <summary>Codecs the client's loop engine can decode.</summary>
    public static readonly string[] LoopableCodecs = ["flac", "vorbis", "opus"];

    private const int MaxEntries = 4096;

    private readonly ConcurrentDictionary<string, FileLoopInfo> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<LoopInfoService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopInfoService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public LoopInfoService(ILogger<LoopInfoService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets loop metadata for a file on disk.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The metadata; <see cref="LoopResolution.HasLoop"/> is false with a reason when not loopable.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public FileLoopInfo Get(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Audio file not found.", path);
        }

        var version = string.Create(CultureInfo.InvariantCulture, $"{file.Length}-{file.LastWriteTimeUtc.Ticks}");
        var key = path + "|" + version;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var info = Read(file, version);
        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        _cache[key] = info;
        return info;
    }

    /// <summary>
    /// Builds loop metadata from an already-parsed header.
    /// </summary>
    /// <param name="header">Parsed header.</param>
    /// <returns>The resolution.</returns>
    public static LoopResolution ResolveHeader(AudioHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (Array.IndexOf(LoopableCodecs, header.Codec) < 0)
        {
            return new LoopResolution { Reason = header.Error ?? $"Unsupported codec '{header.Codec}'." };
        }

        var loop = LoopResolver.Resolve(header.Tags, header.SampleRate, header.TotalSamples);
        if (loop.HasLoop && header.Error is not null)
        {
            // Tags parsed but the header is damaged (e.g. truncated); don't trust it for playback.
            return new LoopResolution
            {
                LoopStart = loop.LoopStart,
                LoopEnd = loop.LoopEnd,
                Convention = loop.Convention,
                RawTags = loop.RawTags,
                Reason = header.Error
            };
        }

        return loop;
    }

    private FileLoopInfo Read(FileInfo file, string version)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024, FileOptions.RandomAccess);
        var header = HeaderParser.Parse(stream);
        var loop = ResolveHeader(header);
        _logger.LogDebug(
            "VGM Loop: {Path}: {Codec} {Rate} Hz total={Total} loop={HasLoop} {Start}..{End} ({Reason})",
            file.FullName,
            header.Codec,
            header.SampleRate,
            header.TotalSamples,
            loop.HasLoop,
            loop.LoopStart,
            loop.LoopEnd,
            loop.Reason ?? loop.Convention);
        return new FileLoopInfo(header, loop, file.Length, version);
    }
}
