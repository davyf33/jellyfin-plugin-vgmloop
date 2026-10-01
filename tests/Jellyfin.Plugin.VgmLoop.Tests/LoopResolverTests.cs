using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VgmLoop.Loop;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

public class LoopResolverTests
{
    private const int Rate = 32000;
    private const long Total = 2349639;

    private static LoopResolution Resolve(long? total, params string[] kv)
        => LoopResolver.Resolve(
            kv.Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal),
            Rate,
            total);

    [Theory]
    [InlineData("197319", 197319L)]
    [InlineData(" 197319 ", 197319L)]
    [InlineData("0", 0L)]
    [InlineData("6.1662", 197318L)] // 197318.4
    [InlineData(".5", 16000L)]
    [InlineData("0:06.16622", 197319L)]
    [InlineData("1:00", 1920000L)]
    [InlineData("1:02:03.5", 119152000L)] // 3723.5 s
    [InlineData("0.0000156", 0L)] // 0.4992 rounds down
    [InlineData("0.000015625", 1L)] // exactly 0.5 rounds up, like Math.round
    public void ParseValue_Accepted(string raw, long expected)
        => Assert.Equal(expected, LoopResolver.ParseValue(raw, Rate));

    [Theory]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("1e5")]
    [InlineData("12s")]
    [InlineData("1,000")]
    [InlineData("1:2:3:4")]
    [InlineData("99999999999999999999999")]
    [InlineData("１２３")] // full-width digits
    public void ParseValue_Rejected(string raw)
        => Assert.Null(LoopResolver.ParseValue(raw, Rate));

    [Fact]
    public void StartLengthAndMatchingEnd_Exclusive()
    {
        var r = Resolve(Total, "LOOPSTART=197319", "LOOPLENGTH=2152320", "LOOPEND=2349639");
        Assert.True(r.HasLoop);
        Assert.Equal((197319L, 2349639L), (r.LoopStart!.Value, r.LoopEnd!.Value));
        Assert.Equal("LOOPEND exclusive (equals start + length)", r.Convention);
        Assert.Equal(3, r.RawTags.Count);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void StartLengthAndInclusiveEnd()
    {
        var r = Resolve(Total, "LOOPSTART=197319", "LOOPLENGTH=2152320", "LOOPEND=2349638");
        Assert.True(r.HasLoop);
        Assert.Equal(2349639, r.LoopEnd);
        Assert.StartsWith("LOOPEND inclusive", r.Convention, StringComparison.Ordinal);
    }

    [Fact]
    public void StartLengthAndDisagreeingEnd_UsesLength()
    {
        var r = Resolve(Total, "LOOPSTART=197319", "LOOPLENGTH=2000000", "LOOPEND=2349639");
        Assert.True(r.HasLoop);
        Assert.Equal(2197319, r.LoopEnd);
        Assert.StartsWith("LOOPEND disagrees", r.Convention, StringComparison.Ordinal);
    }

    [Fact]
    public void StartAndLength()
    {
        var r = Resolve(Total, "LOOPSTART=100", "LOOPLENGTH=32000");
        Assert.True(r.HasLoop);
        Assert.Equal(32100, r.LoopEnd);
        Assert.Equal("start + length", r.Convention);
    }

    [Fact]
    public void UnderscoreSpellings()
    {
        var r = Resolve(Total, "LOOP_START=197319", "LOOP_END=2349639");
        Assert.True(r.HasLoop);
        Assert.Equal((197319L, 2349639L), (r.LoopStart!.Value, r.LoopEnd!.Value));
        Assert.Equal("LOOPEND only, treated as exclusive", r.Convention);
        Assert.Equal(new[] { "LOOP_END", "LOOP_START" }, r.RawTags.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NoUnderscoreWinsOverUnderscore()
    {
        var r = Resolve(Total, "LOOP_START=5", "LOOPSTART=197319");
        Assert.Equal(197319, r.LoopStart);
    }

    [Fact]
    public void EndOnly_LastSampleIndex_NotedButExclusive()
    {
        var r = Resolve(Total, "LOOPSTART=197319", "LOOPEND=2349638");
        Assert.True(r.HasLoop);
        Assert.Equal(2349638, r.LoopEnd);
        Assert.Contains("probably inclusive", r.Convention, StringComparison.Ordinal);
    }

    [Fact]
    public void StartOnly_LoopsToEnd()
    {
        var r = Resolve(Total, "LOOPSTART=197319");
        Assert.True(r.HasLoop);
        Assert.Equal(Total, r.LoopEnd);
        Assert.StartsWith("no end tag", r.Convention, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondsAndTimestamps()
    {
        var r = Resolve(Total, "LOOPSTART=6.16621875", "LOOPEND=1:13.42621875");
        Assert.True(r.HasLoop);
        Assert.Equal((197319L, 2349639L), (r.LoopStart!.Value, r.LoopEnd!.Value));
    }

    [Fact]
    public void UnparsedLengthFallsBackToEnd()
    {
        var r = Resolve(Total, "LOOPSTART=197319", "LOOPLENGTH=abc", "LOOPEND=2349639");
        Assert.True(r.HasLoop);
        Assert.Equal(2349639, r.LoopEnd);
    }

    [Theory]
    [InlineData("No loop start tag.", "LOOPLENGTH=100")]
    [InlineData("Loop start value could not be parsed.", "LOOPSTART=soon")]
    [InlineData("outside", "LOOPSTART=2349639")] // start == end (start only)
    [InlineData("outside", "LOOPSTART=1000", "LOOPEND=500")]
    [InlineData("outside", "LOOPSTART=1000", "LOOPLENGTH=2349639")] // end > total
    [InlineData("shorter than 0.1 s", "LOOPSTART=1000", "LOOPLENGTH=3199")]
    public void Invalid(string reason, params string[] tags)
    {
        var r = Resolve(Total, tags);
        Assert.False(r.HasLoop);
        Assert.Contains(reason, r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyTenthOfASecond_IsValid()
        => Assert.True(Resolve(Total, "LOOPSTART=1000", "LOOPLENGTH=3200").HasLoop);

    [Fact]
    public void UnknownTotal_NoLoop()
    {
        var r = Resolve(null, "LOOPSTART=1000", "LOOPLENGTH=32000");
        Assert.False(r.HasLoop);
        Assert.Equal(33000, r.LoopEnd);
        Assert.Equal("Track length is unknown.", r.Reason);
    }

    [Fact]
    public void ResolveHeader_UnsupportedCodec()
    {
        var r = LoopInfoService.ResolveHeader(new AudioHeader { Error = "Not a FLAC or Ogg file." });
        Assert.False(r.HasLoop);
        Assert.Equal("Not a FLAC or Ogg file.", r.Reason);
    }

    [Fact]
    public void ResolveHeader_DamagedHeaderDisablesLoop()
    {
        var r = LoopInfoService.ResolveHeader(new AudioHeader
        {
            Codec = "vorbis",
            SampleRate = Rate,
            TotalSamples = Total,
            Tags = new Dictionary<string, string> { ["LOOPSTART"] = "197319" },
            Error = "Could not find the final Ogg page (truncated file?)."
        });
        Assert.False(r.HasLoop);
        Assert.Equal(197319, r.LoopStart);
        Assert.StartsWith("Could not find", r.Reason, StringComparison.Ordinal);
    }
}
