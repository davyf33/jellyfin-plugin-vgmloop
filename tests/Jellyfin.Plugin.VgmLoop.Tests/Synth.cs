using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.VgmLoop.Tests;

/// <summary>
/// Builds minimal but structurally valid FLAC / Ogg byte streams for parser tests. No real audio frames.
/// </summary>
internal static class Synth
{
    public static byte[] Comments(IEnumerable<string> entries, string vendor = "synth")
    {
        var list = entries.ToList();
        using var ms = new MemoryStream();
        WriteLe32(ms, (uint)Encoding.UTF8.GetByteCount(vendor));
        ms.Write(Encoding.UTF8.GetBytes(vendor));
        WriteLe32(ms, (uint)list.Count);
        foreach (var e in list)
        {
            var b = Encoding.UTF8.GetBytes(e);
            WriteLe32(ms, (uint)b.Length);
            ms.Write(b);
        }

        return ms.ToArray();
    }

    public static byte[] Id3(int bodySize, bool footer = false)
    {
        var tag = new byte[10 + bodySize + (footer ? 10 : 0)];
        "ID3"u8.CopyTo(tag);
        tag[3] = footer ? (byte)4 : (byte)3;
        tag[5] = footer ? (byte)0x10 : (byte)0;
        tag[6] = (byte)((bodySize >> 21) & 0x7f);
        tag[7] = (byte)((bodySize >> 14) & 0x7f);
        tag[8] = (byte)((bodySize >> 7) & 0x7f);
        tag[9] = (byte)(bodySize & 0x7f);
        return tag;
    }

    public static byte[] Flac(int rate, int channels, int bits, long total, IEnumerable<string> tags, int pictureBytes = 0, byte[]? prefix = null)
    {
        using var ms = new MemoryStream();
        if (prefix is not null)
        {
            ms.Write(prefix);
        }

        ms.Write("fLaC"u8);
        var si = new byte[34];
        si[10] = (byte)(rate >> 12);
        si[11] = (byte)(rate >> 4);
        si[12] = (byte)(((rate & 0xf) << 4) | ((channels - 1) << 1) | ((bits - 1) >> 4));
        si[13] = (byte)((((bits - 1) & 0xf) << 4) | (int)((total >> 32) & 0xf));
        BinaryPrimitives.WriteUInt32BigEndian(si.AsSpan(14), (uint)(total & 0xffffffff));
        Block(ms, 0, si, last: false);
        if (pictureBytes > 0)
        {
            Block(ms, 6, new byte[pictureBytes], last: false);
        }

        Block(ms, 1, new byte[16], last: false); // padding
        Block(ms, 4, Comments(tags), last: true);
        ms.Write(new byte[] { 0xff, 0xf8, 0x00, 0x00 }); // start of a fake frame
        return ms.ToArray();
    }

    public static byte[] OpusHead(int channels, int preSkip, int inputRate)
    {
        var h = new byte[19];
        "OpusHead"u8.CopyTo(h);
        h[8] = 1;
        h[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)inputRate);
        return h;
    }

    public static byte[] OpusTags(IEnumerable<string> tags) => [.. "OpusTags"u8.ToArray(), .. Comments(tags)];

    public static byte[] VorbisId(int channels, int rate)
    {
        var h = new byte[30];
        h[0] = 1;
        "vorbis"u8.CopyTo(h.AsSpan(1));
        h[11] = (byte)channels;
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)rate);
        h[29] = 1;
        return h;
    }

    public static byte[] VorbisComments(IEnumerable<string> tags, int extraPadding = 0)
    {
        byte[] body = [3, .. "vorbis"u8.ToArray(), .. Comments(tags)];
        return [.. body, 1, .. new byte[extraPadding]]; // framing bit (+ optional bulk)
    }

    /// <summary>
    /// Builds an Ogg stream: the header packets (each starting a new page, as muxers do), then
    /// <paramref name="audioPages"/> small audio pages ending at <paramref name="finalGranule"/>.
    /// </summary>
    public static byte[] Ogg(IReadOnlyList<byte[]> headerPackets, long finalGranule, uint serial = 0x1234, int audioPages = 3, int audioPageBytes = 1000)
    {
        using var ms = new MemoryStream();
        uint seq = 0;
        var first = true;
        foreach (var packet in headerPackets)
        {
            WritePacket(ms, packet, serial, ref seq, granule: 0, bos: first);
            first = false;
        }

        for (var i = 1; i <= audioPages; i++)
        {
            var granule = i == audioPages ? finalGranule : finalGranule * i / (audioPages + 1);
            WritePage(ms, [.. Laces(audioPageBytes)], new byte[audioPageBytes], serial, seq++, granule, bos: false, eos: i == audioPages, continued: false);
        }

        return ms.ToArray();
    }

    private static List<byte> Laces(int packetLength)
    {
        // A packet is 255-byte segments plus a final segment < 255 (0 when the length is a multiple of 255).
        var laces = new List<byte>();
        var left = packetLength;
        while (left >= 255)
        {
            laces.Add(255);
            left -= 255;
        }

        laces.Add((byte)left);
        return laces;
    }

    private static void WritePacket(MemoryStream ms, byte[] packet, uint serial, ref uint seq, long granule, bool bos)
    {
        var laces = Laces(packet.Length);
        var off = 0;
        for (var i = 0; i < laces.Count; i += 255)
        {
            var pageLaces = laces.Skip(i).Take(255).ToArray();
            var bodyLen = pageLaces.Sum(l => l);
            var endsPacket = pageLaces[^1] < 255;
            WritePage(ms, pageLaces, packet.AsSpan(off, bodyLen).ToArray(), serial, seq++, endsPacket ? granule : -1, bos && i == 0, eos: false, continued: i > 0);
            off += bodyLen;
        }
    }

    private static void WritePage(MemoryStream ms, byte[] laces, byte[] body, uint serial, uint seq, long granule, bool bos, bool eos, bool continued)
    {
        var h = new byte[27];
        "OggS"u8.CopyTo(h);
        h[5] = (byte)((continued ? 1 : 0) | (bos ? 2 : 0) | (eos ? 4 : 0));
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(18), seq);
        h[26] = (byte)laces.Length;
        ms.Write(h);
        ms.Write(laces);
        ms.Write(body);
    }

    private static void Block(MemoryStream ms, int type, byte[] body, bool last)
    {
        ms.WriteByte((byte)((last ? 0x80 : 0) | type));
        ms.WriteByte((byte)(body.Length >> 16));
        ms.WriteByte((byte)(body.Length >> 8));
        ms.WriteByte((byte)body.Length);
        ms.Write(body);
    }

    private static void WriteLe32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }
}
