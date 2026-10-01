using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.VgmLoop.Loop;

/// <summary>
/// Minimal FLAC / Ogg (Vorbis, Opus) header and Vorbis-comment reader.
/// Port of <c>parseHeader</c>/<c>parseOgg</c>/<c>parseComments</c> from <c>tools/loop-seam-probe.html</c>.
/// Reads only the metadata it needs from the start of the file plus the last 256 KB (for the final Ogg granule).
/// Never throws on malformed input; problems are reported in <see cref="AudioHeader.Error"/>.
/// </summary>
public static class HeaderParser
{
    /// <summary>How far back from EOF to look for the last Ogg page.</summary>
    public const int TailScanBytes = 256 * 1024;

    /// <summary>Largest FLAC metadata block or Ogg header packet we are willing to buffer (large cover art).</summary>
    public const int MaxMetadataBytes = 32 * 1024 * 1024;

    private const int MaxOggPages = 4000;
    private const int MaxFlacBlocks = 128;

    /// <summary>
    /// Parses the header of a seekable audio stream.
    /// </summary>
    /// <param name="stream">Seekable stream positioned anywhere.</param>
    /// <returns>The parsed header.</returns>
    public static AudioHeader Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable.", nameof(stream));
        }

        var offset = SkipId3(stream);
        Span<byte> magic = stackalloc byte[4];
        if (!TryReadAt(stream, offset, magic))
        {
            return new AudioHeader { AudioDataOffset = offset, Error = "File is too short to contain an audio header." };
        }

        if (magic.SequenceEqual("fLaC"u8))
        {
            return ParseFlac(stream, offset);
        }

        if (magic.SequenceEqual("OggS"u8))
        {
            return ParseOgg(stream, offset);
        }

        return new AudioHeader { AudioDataOffset = offset, Error = "Not a FLAC or Ogg file." };
    }

    /// <summary>
    /// Parses a Vorbis comment block (vendor string, count, <c>KEY=value</c> entries, all little-endian lengths).
    /// Keys are upper-cased; the first occurrence of a key wins. Stops quietly at truncation.
    /// </summary>
    /// <param name="data">Buffer holding the comment block.</param>
    /// <param name="offset">Offset of the vendor length field.</param>
    /// <returns>The tags.</returns>
    public static Dictionary<string, string> ParseComments(ReadOnlySpan<byte> data, int offset)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        long p = offset;
        if (p + 4 > data.Length)
        {
            return tags;
        }

        p += 4 + (long)BinaryPrimitives.ReadUInt32LittleEndian(data[(int)p..]);
        if (p + 4 > data.Length)
        {
            return tags;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)p..]);
        p += 4;
        for (uint i = 0; i < count && p + 4 <= data.Length; i++)
        {
            long len = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)p..]);
            p += 4;
            if (p + len > data.Length)
            {
                break;
            }

            var entry = Encoding.UTF8.GetString(data.Slice((int)p, (int)len));
            p += len;
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                tags.TryAdd(entry[..eq].ToUpperInvariant(), entry[(eq + 1)..]);
            }
        }

        return tags;
    }

    private static long SkipId3(Stream stream)
    {
        long offset = 0;
        Span<byte> h = stackalloc byte[10];

        // Some taggers stack more than one ID3v2 tag.
        for (var i = 0; i < 4 && TryReadAt(stream, offset, h) && h[..3].SequenceEqual("ID3"u8); i++)
        {
            var size = ((h[6] & 0x7f) << 21) | ((h[7] & 0x7f) << 14) | ((h[8] & 0x7f) << 7) | (h[9] & 0x7f);
            offset += 10 + size + ((h[5] & 0x10) != 0 ? 10 : 0);
        }

        return offset;
    }

    private static AudioHeader ParseFlac(Stream stream, long offset)
    {
        long pos = offset + 4;
        Span<byte> hdr = stackalloc byte[4];
        Span<byte> si = stackalloc byte[34];
        int rate = 0, channels = 0, bits = 0;
        long total = 0;
        var haveStreamInfo = false;
        IReadOnlyDictionary<string, string> tags = new Dictionary<string, string>(StringComparer.Ordinal);
        string? error = null;

        for (var guard = 0; guard < MaxFlacBlocks; guard++)
        {
            if (!TryReadAt(stream, pos, hdr))
            {
                error = "FLAC metadata is truncated.";
                break;
            }

            var type = hdr[0] & 0x7f;
            var last = (hdr[0] & 0x80) != 0;
            var len = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            var body = pos + 4;

            if (type == 0)
            {
                if (len < 34 || !TryReadAt(stream, body, si))
                {
                    error = "FLAC STREAMINFO is truncated.";
                    break;
                }

                rate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
                channels = ((si[12] >> 1) & 7) + 1;
                bits = (((si[12] & 1) << 4) | (si[13] >> 4)) + 1;
                total = ((long)(si[13] & 0x0f) << 32) | BinaryPrimitives.ReadUInt32BigEndian(si[14..]);
                haveStreamInfo = true;
            }
            else if (type == 4)
            {
                var block = new byte[len];
                if (!TryReadAt(stream, body, block))
                {
                    error = "FLAC VORBIS_COMMENT block is truncated.";
                    break;
                }

                tags = ParseComments(block, 0);
            }

            if (last)
            {
                break;
            }

            pos = body + len;
        }

        if (!haveStreamInfo)
        {
            error ??= "FLAC STREAMINFO block not found.";
        }

        return new AudioHeader
        {
            Container = "flac",
            Codec = "flac",
            SampleRate = rate,
            Channels = channels,
            BitsPerSample = haveStreamInfo ? bits : null,
            TotalSamples = total > 0 ? total : null,
            AudioDataOffset = offset,
            Tags = tags,
            Error = error
        };
    }

    private static AudioHeader ParseOgg(Stream stream, long offset)
    {
        var packets = new List<byte[]>(2);
        var cur = new MemoryStream();
        uint? serial = null;
        long pos = offset;
        byte[] header = new byte[27];
        byte[] lacing = new byte[255];
        string? error = null;

        for (var pages = 0; pages < MaxOggPages && packets.Count < 2; pages++)
        {
            if (!TryReadAt(stream, pos, header) || !header.AsSpan(0, 4).SequenceEqual("OggS"u8))
            {
                break;
            }

            var ser = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
            int nseg = header[26];
            var lace = lacing.AsSpan(0, nseg);
            if (!TryReadAt(stream, pos + 27, lace))
            {
                break;
            }

            var bodyLen = 0;
            foreach (var l in lace)
            {
                bodyLen += l;
            }

            var bodyStart = pos + 27 + nseg;
            serial ??= ser;
            if (ser == serial)
            {
                var body = new byte[bodyLen];
                if (!TryReadAt(stream, bodyStart, body))
                {
                    break;
                }

                var b = 0;
                foreach (var l in lace)
                {
                    cur.Write(body, b, l);
                    b += l;
                    if (l < 255 && packets.Count < 2)
                    {
                        packets.Add(cur.ToArray());
                        cur.SetLength(0);
                    }
                }

                if (cur.Length > MaxMetadataBytes)
                {
                    error = "Ogg header packet exceeds the size limit.";
                    break;
                }
            }

            pos = bodyStart + bodyLen;
        }

        var head = packets.Count > 0 ? packets[0] : [];
        var comments = packets.Count > 1 ? packets[1] : null;
        string codec = "unknown";
        int rate = 0, channels = 0;
        int? preSkip = null, inputRate = null;
        IReadOnlyDictionary<string, string> tags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (head.Length >= 19 && head.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            codec = "opus";
            channels = head[9];
            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
            inputRate = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12)));
            rate = 48000;
            if (comments is not null && comments.Length >= 8 && comments.AsSpan(0, 8).SequenceEqual("OpusTags"u8))
            {
                tags = ParseComments(comments, 8);
            }
        }
        else if (head.Length >= 16 && head[0] == 1 && head.AsSpan(1, 6).SequenceEqual("vorbis"u8))
        {
            codec = "vorbis";
            channels = head[11];
            rate = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12)));
            if (comments is not null && comments.Length >= 7 && comments[0] == 3 && comments.AsSpan(1, 6).SequenceEqual("vorbis"u8))
            {
                tags = ParseComments(comments, 7);
            }
        }
        else
        {
            error ??= head.Length == 0 ? "Ogg identification header not found (truncated file?)." : "Unsupported Ogg codec.";
        }

        if (codec != "unknown" && comments is null)
        {
            error ??= "Ogg comment header is incomplete (truncated file?).";
        }

        long? total = null;
        if (serial is not null && codec != "unknown")
        {
            var granule = FindLastGranule(stream, serial.Value);
            if (granule is not null)
            {
                var t = codec == "opus" ? granule.Value - (preSkip ?? 0) : granule.Value;
                total = t > 0 ? t : null;
            }
            else
            {
                error ??= "Could not find the final Ogg page (truncated file?).";
            }
        }

        return new AudioHeader
        {
            Container = "ogg",
            Codec = codec,
            SampleRate = rate,
            Channels = channels,
            TotalSamples = total,
            PreSkip = preSkip,
            InputSampleRate = inputRate,
            AudioDataOffset = offset,
            Tags = tags,
            Error = error
        };
    }

    private static long? FindLastGranule(Stream stream, uint serial)
    {
        var start = Math.Max(0, stream.Length - TailScanBytes);
        var tail = new byte[stream.Length - start];
        if (!TryReadAt(stream, start, tail))
        {
            return null;
        }

        for (var i = tail.Length - 27; i >= 0; i--)
        {
            if (tail[i] == 0x4f && tail[i + 1] == 0x67 && tail[i + 2] == 0x67 && tail[i + 3] == 0x53 && tail[i + 4] == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 14)) == serial)
            {
                var granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 6));
                if (granule >= 0)
                {
                    return granule;
                }
            }
        }

        return null;
    }

    private static bool TryReadAt(Stream stream, long position, Span<byte> buffer)
    {
        if (position < 0 || position + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = position;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
