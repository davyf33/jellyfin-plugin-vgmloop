using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.VgmLoop.Discs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

public class DiscNamesTests
{
    private static Dictionary<string, string> Fields(params string[] kv)
        => kv.Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

    [Theory]
    [InlineData("DISCSUBTITLE=Arrange Tracks", "Arrange Tracks")]
    [InlineData("discsubtitle=Arrange Tracks", "Arrange Tracks")] // ATL keeps some formats' original casing
    [InlineData("TSST=Original Soundtrack", "Original Soundtrack")]
    [InlineData("SETSUBTITLE=Bonus", "Bonus")]
    [InlineData("DISCNAME=  Padded  ", "Padded")]
    [InlineData("DISCSUBTITLE=First\u001FSecond", "First")]
    [InlineData("DISCSUBTITLE=Cut\0garbage", "Cut")]
    public void FromFields_FindsName(string field, string expected)
        => Assert.Equal(expected, DiscNames.FromFields(Fields(field)));

    [Fact]
    public void FromFields_PrefersDiscSubtitleOverTsst()
        => Assert.Equal("Picard", DiscNames.FromFields(Fields("TSST=Frame", "DISCSUBTITLE=Picard")));

    [Fact]
    public void FromFields_SkipsEmptyPreferredKey()
        => Assert.Equal("Frame", DiscNames.FromFields(Fields("DISCSUBTITLE=  ", "TSST=Frame")));

    [Theory]
    [InlineData("TITLE=Not a disc name")]
    [InlineData("DISCSUBTITLE=")]
    [InlineData("DISCNUMBER=2")]
    public void FromFields_NoName(string field)
        => Assert.Null(DiscNames.FromFields(Fields(field)));

    [Fact]
    public void Clean_CapsLength()
        => Assert.Equal(DiscNames.MaxNameLength, DiscNames.Clean(new string('x', 500))!.Length);

    [Fact]
    public void Build_GroupsByDiscAndNamesFromFirstTaggedTrack()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var tracks = new[]
        {
            new DiscTrack(ids[3], 2, 2, "/a/2-02.flac"),
            new DiscTrack(ids[0], 1, 1, "/a/1-01.flac"),
            new DiscTrack(ids[1], 1, 2, "/a/1-02.flac"),
            new DiscTrack(ids[2], 2, 1, "/a/2-01.flac"),
            new DiscTrack(ids[4], 3, 1, "/a/3-01.flac"),
            new DiscTrack(ids[5], null, null, "/a/loose.flac"),
        };
        var names = new Dictionary<string, string?>
        {
            ["/a/1-01.flac"] = "Original",
            ["/a/2-01.flac"] = null, // first track untagged, the next one has it
            ["/a/2-02.flac"] = "Arrange",
            ["/a/loose.flac"] = "Ignored",
        };

        var discs = DiscNames.Build(tracks, t => names.GetValueOrDefault(t.Path));

        Assert.Collection(
            discs,
            d =>
            {
                Assert.Equal(1, d.DiscNumber);
                Assert.Equal("Original", d.Name);
                Assert.Equal([ids[0], ids[1]], d.ItemIds);
            },
            d =>
            {
                Assert.Equal(2, d.DiscNumber);
                Assert.Equal("Arrange", d.Name);
                Assert.Equal([ids[2], ids[3]], d.ItemIds);
            });
    }

    [Fact]
    public void Build_ReadsAtMostThreeTracksPerDisc()
    {
        var tracks = Enumerable.Range(1, 10).Select(i => new DiscTrack(Guid.NewGuid(), 1, i, $"/a/{i:00}.flac")).ToList();
        var reads = 0;
        var discs = DiscNames.Build(tracks, t =>
        {
            reads++;
            return t.TrackNumber == 5 ? "Too late" : null;
        });

        Assert.Empty(discs);
        Assert.Equal(DiscNames.MaxTracksReadPerDisc, reads);
    }

    [Fact]
    public void Service_ReadsDiscSubtitleFromFlac()
    {
        var path = WriteTemp(".flac", Synth.Flac(44100, 2, 16, 441000, ["TITLE=Field", "DISCNUMBER=2", "DISCSUBTITLE=Arrange Album"]));
        try
        {
            var service = new DiscInfoService(NullLogger<DiscInfoService>.Instance);
            Assert.Equal("Arrange Album", service.GetDiscName(path));
            Assert.Equal("Arrange Album", service.GetDiscName(path)); // cached
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Service_ReadsTsstFromMp3()
    {
        var path = WriteTemp(".mp3", Mp3WithId3Frames(("TIT2", "Field"), ("TPOS", "2/2"), ("TSST", "Sound Test")));
        try
        {
            var service = new DiscInfoService(NullLogger<DiscInfoService>.Instance);
            Assert.Equal("Sound Test", service.GetDiscName(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Service_NoNameAndNoThrowForMissingOrUntaggedFiles()
    {
        var service = new DiscInfoService(NullLogger<DiscInfoService>.Instance);
        Assert.Null(service.GetDiscName(Path.Combine(Path.GetTempPath(), "vgmloop-missing-" + Guid.NewGuid() + ".flac")));

        var path = WriteTemp(".flac", Synth.Flac(44100, 2, 16, 441000, ["TITLE=Field"]));
        try
        {
            Assert.Null(service.GetDiscName(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemp(string extension, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "vgmloop-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>An ID3v2.4 tag with UTF-8 text frames, followed by a few silent MPEG-1 Layer III frames.</summary>
    private static byte[] Mp3WithId3Frames(params (string Id, string Text)[] frames)
    {
        using var body = new MemoryStream();
        foreach (var (id, text) in frames)
        {
            var payload = new byte[] { 3 }.Concat(Encoding.UTF8.GetBytes(text)).ToArray(); // 3 = UTF-8
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write(SyncSafe(payload.Length));
            body.Write(new byte[2]); // flags
            body.Write(payload);
        }

        using var ms = new MemoryStream();
        ms.Write("ID3"u8);
        ms.Write(new byte[] { 4, 0, 0 }); // v2.4.0, no flags
        ms.Write(SyncSafe((int)body.Length));
        body.WriteTo(ms);

        // 128 kbit/s, 44.1 kHz, no padding: 417-byte frames.
        for (var i = 0; i < 8; i++)
        {
            var frame = new byte[417];
            frame[0] = 0xff;
            frame[1] = 0xfb;
            frame[2] = 0x90;
            frame[3] = 0x64;
            ms.Write(frame);
        }

        return ms.ToArray();
    }

    private static byte[] SyncSafe(int n)
        => [(byte)((n >> 21) & 0x7f), (byte)((n >> 14) & 0x7f), (byte)((n >> 7) & 0x7f), (byte)(n & 0x7f)];
}
