using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.VgmLoop.Loop;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

public class HeaderParserTests
{
    private static readonly string[] WiiTags = ["TITLE=Menu", "LOOPSTART=197319", "LOOPLENGTH=2152320", "LOOPEND=2349639"];

    private static AudioHeader Parse(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return HeaderParser.Parse(ms);
    }

    [Fact]
    public void Flac_StreamInfoAndComments()
    {
        var h = Parse(Synth.Flac(32000, 2, 16, 2349639, WiiTags));
        Assert.Null(h.Error);
        Assert.Equal(("flac", "flac"), (h.Container, h.Codec));
        Assert.Equal(32000, h.SampleRate);
        Assert.Equal(2, h.Channels);
        Assert.Equal(16, h.BitsPerSample);
        Assert.Equal(2349639, h.TotalSamples);
        Assert.Equal(0, h.AudioDataOffset);
        Assert.Equal("197319", h.Tags["LOOPSTART"]);
    }

    [Fact]
    public void Flac_HighRateAnd36BitTotal()
    {
        var h = Parse(Synth.Flac(192000, 6, 24, (1L << 33) + 5, []));
        Assert.Equal(192000, h.SampleRate);
        Assert.Equal(6, h.Channels);
        Assert.Equal(24, h.BitsPerSample);
        Assert.Equal((1L << 33) + 5, h.TotalSamples);
    }

    [Theory]
    [InlineData(5812, false, 5822)]
    [InlineData(100, true, 120)]
    public void Flac_SkipsId3(int id3Body, bool footer, long expectedOffset)
    {
        var h = Parse(Synth.Flac(32000, 2, 16, 1000, WiiTags, prefix: Synth.Id3(id3Body, footer)));
        Assert.Null(h.Error);
        Assert.Equal(expectedOffset, h.AudioDataOffset);
        Assert.Equal("flac", h.Codec);
        Assert.Equal("2152320", h.Tags["LOOPLENGTH"]);
    }

    [Fact]
    public void Flac_LargePictureBeforeComments()
    {
        var h = Parse(Synth.Flac(44100, 2, 16, 44100 * 60, WiiTags, pictureBytes: 3 * 1024 * 1024));
        Assert.Null(h.Error);
        Assert.Equal("197319", h.Tags["LOOPSTART"]);
    }

    [Fact]
    public void Comments_KeysUpperCasedFirstOccurrenceWins()
    {
        var h = Parse(Synth.Flac(32000, 2, 16, 1000, ["loopstart=10", "LoopStart=20", "Loop_End=900", "NOEQUALS", "=novalue", "ARTIST=a=b"]));
        Assert.Equal("10", h.Tags["LOOPSTART"]);
        Assert.Equal("900", h.Tags["LOOP_END"]);
        Assert.Equal("a=b", h.Tags["ARTIST"]);
        Assert.Equal(3, h.Tags.Count);
    }

    [Fact]
    public void Comments_Utf8()
    {
        var h = Parse(Synth.Flac(32000, 2, 16, 1000, ["TITLE=Mii チャンネル"]));
        Assert.Equal("Mii チャンネル", h.Tags["TITLE"]);
    }

    [Fact]
    public void Opus_TotalExcludesPreSkip()
    {
        var bytes = Synth.Ogg([Synth.OpusHead(2, 312, 32000), Synth.OpusTags(["LOOPSTART=295979", "LOOPLENGTH=3228480"])], finalGranule: 3524459 + 312);
        var h = Parse(bytes);
        Assert.Null(h.Error);
        Assert.Equal(("ogg", "opus"), (h.Container, h.Codec));
        Assert.Equal(48000, h.SampleRate);
        Assert.Equal(32000, h.InputSampleRate);
        Assert.Equal(2, h.Channels);
        Assert.Equal(312, h.PreSkip);
        Assert.Equal(3524459, h.TotalSamples);
        Assert.Equal("295979", h.Tags["LOOPSTART"]);
    }

    [Fact]
    public void Vorbis_TotalIsGranule()
    {
        var h = Parse(Synth.Ogg([Synth.VorbisId(2, 32000), Synth.VorbisComments(WiiTags), new byte[] { 5, 1, 2, 3 }], finalGranule: 2349639));
        Assert.Null(h.Error);
        Assert.Equal(("ogg", "vorbis"), (h.Container, h.Codec));
        Assert.Equal(32000, h.SampleRate);
        Assert.Equal(2349639, h.TotalSamples);
        Assert.Null(h.PreSkip);
        Assert.Equal("2349639", h.Tags["LOOPEND"]);
    }

    [Theory]
    [InlineData(255 * 255 * 3)] // exactly fills pages; ends with a zero lace on its own page
    [InlineData(600_000)] // large embedded cover art spanning many pages
    public void Ogg_CommentPacketSpanningPages(int padding)
    {
        var comments = Synth.VorbisComments(WiiTags, extraPadding: padding);
        var h = Parse(Synth.Ogg([Synth.VorbisId(2, 32000), comments], finalGranule: 2349639));
        Assert.Null(h.Error);
        Assert.Equal("197319", h.Tags["LOOPSTART"]);
        Assert.Equal(2349639, h.TotalSamples);
    }

    [Fact]
    public void Ogg_CoverArtBeforeLoopTags()
    {
        var picture = "METADATA_BLOCK_PICTURE=" + new string('A', 400_000);
        var bytes = Synth.Ogg([Synth.OpusHead(2, 312, 48000), Synth.OpusTags([picture, "LOOPSTART=1000", "LOOPLENGTH=96000"])], finalGranule: 200_312);
        var h = Parse(bytes);
        Assert.Null(h.Error);
        Assert.Equal("1000", h.Tags["LOOPSTART"]);
        Assert.Equal(200_000, h.TotalSamples);
    }

    [Fact]
    public void Ogg_LastGranuleFoundBeyondIgnoredPagesOfOtherStreams()
    {
        var main = Synth.Ogg([Synth.VorbisId(2, 44100), Synth.VorbisComments([])], finalGranule: 441000, serial: 7);
        var other = Synth.Ogg([Synth.VorbisId(1, 8000), Synth.VorbisComments([])], finalGranule: 99, serial: 8);
        var h = Parse([.. main, .. other]);
        Assert.Equal(441000, h.TotalSamples);
    }

    [Fact]
    public void Ogg_WithId3Prefix()
    {
        var h = Parse([.. Synth.Id3(50), .. Synth.Ogg([Synth.OpusHead(1, 3840, 44100), Synth.OpusTags([])], finalGranule: 48000 + 3840)]);
        Assert.Equal(60, h.AudioDataOffset);
        Assert.Equal(48000, h.TotalSamples);
    }

    [Fact]
    public void Truncated_FlacInsideComments()
    {
        var full = Synth.Flac(32000, 2, 16, 2349639, WiiTags);
        var h = Parse(full.AsSpan(0, full.Length - 30).ToArray());
        Assert.NotNull(h.Error);
        Assert.Equal(32000, h.SampleRate);
        Assert.Empty(h.Tags);
    }

    [Fact]
    public void Truncated_FlacInsideStreamInfo()
    {
        var h = Parse(Synth.Flac(32000, 2, 16, 1000, []).AsSpan(0, 20).ToArray());
        Assert.NotNull(h.Error);
        Assert.Null(h.TotalSamples);
    }

    [Fact]
    public void Truncated_OggInsideCommentPacket()
    {
        var full = Synth.Ogg([Synth.VorbisId(2, 32000), Synth.VorbisComments(WiiTags, extraPadding: 100_000)], finalGranule: 2349639);
        var h = Parse(full.AsSpan(0, 50_000).ToArray());
        Assert.Equal("vorbis", h.Codec);
        Assert.NotNull(h.Error);
        Assert.Empty(h.Tags);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x49, 0x44, 0x33 })]
    [InlineData(new byte[] { 0xff, 0xfb, 0x90, 0x00, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x4f, 0x67, 0x67, 0x53 })]
    public void Garbage_DoesNotThrow(byte[] bytes)
    {
        var h = Parse(bytes);
        Assert.NotNull(h.Error);
        Assert.Equal("unknown", h.Codec);
        Assert.False(LoopInfoService.ResolveHeader(h).HasLoop);
    }

    [Fact]
    public void Ogg_UnsupportedCodec()
    {
        var h = Parse(Synth.Ogg([[0x7f, .. "FLAC"u8.ToArray(), 1, 0], [4, 0, 0, 0]], finalGranule: 1000));
        Assert.Equal("ogg", h.Container);
        Assert.Equal("unknown", h.Codec);
        Assert.Equal("Unsupported Ogg codec.", h.Error);
    }
}
