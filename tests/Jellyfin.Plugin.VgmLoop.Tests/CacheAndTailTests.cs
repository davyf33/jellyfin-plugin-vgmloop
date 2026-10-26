using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VgmLoop.Loop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

public class CacheAndTailTests
{
    private static readonly string[] WiiTags = ["LOOPSTART=197319", "LOOPLENGTH=2152320", "DISCSUBTITLE=Channels", "PLATFORM=Wii"];

    [Fact]
    public void Ogg_LongFile_FoundInQuickWindow()
    {
        var bytes = Synth.Ogg([Synth.VorbisId(2, 32000), Synth.VorbisComments(WiiTags)], finalGranule: 2349639, audioPages: 200, audioPageBytes: 1000);
        Assert.True(bytes.Length > HeaderParser.QuickTailScanBytes * 2);
        var h = HeaderParser.Parse(new MemoryStream(bytes));
        Assert.Null(h.Error);
        Assert.Equal(2349639, h.TotalSamples);
    }

    [Fact]
    public void Ogg_TrailingJunk_FallsBackToWideWindow()
    {
        var ogg = Synth.Ogg([Synth.VorbisId(2, 32000), Synth.VorbisComments(WiiTags)], finalGranule: 2349639, audioPages: 200, audioPageBytes: 1000);
        var bytes = ogg.Concat(new byte[HeaderParser.QuickTailScanBytes + 1000]).ToArray();
        var h = HeaderParser.Parse(new MemoryStream(bytes));
        Assert.Null(h.Error);
        Assert.Equal(2349639, h.TotalSamples);
    }

    [Fact]
    public void FileCache_ReadsOnceUntilTheFileChanges()
    {
        var path = DiscNamesTests.WriteTemp(".bin", [1, 2, 3]);
        try
        {
            var cache = new FileCache<int>(100);
            var reads = 0;
            int Read(FileInfo f, string v) => Interlocked.Increment(ref reads);

            Parallel.For(0, 16, _ => cache.GetOrRead(new FileInfo(path), Read));
            Assert.Equal(1, reads);

            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Equal(2, cache.GetOrRead(new FileInfo(path), Read));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileCache_DoesNotKeepFailures()
    {
        var path = DiscNamesTests.WriteTemp(".bin", [1]);
        try
        {
            var cache = new FileCache<int>(100);
            Assert.Throws<IOException>(() => cache.GetOrRead(new FileInfo(path), (_, _) => throw new IOException("busy")));
            Assert.Equal(7, cache.GetOrRead(new FileInfo(path), (_, _) => 7));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoopInfo_CarriesDiscNameAndPlatform_AndDropsFullComments()
    {
        var path = DiscNamesTests.WriteTemp(".flac", Synth.Flac(32000, 2, 16, 2349639, WiiTags));
        try
        {
            var info = new LoopInfoService(NullLogger<LoopInfoService>.Instance).Get(path);
            Assert.True(info.Loop.HasLoop, info.Loop.Reason);
            Assert.Equal("Channels", info.Tags.DiscName);
            Assert.Equal(["Wii"], info.Tags.Platforms);
            Assert.Empty(info.Header.Tags);
            Assert.Equal(2, info.Loop.RawTags.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
