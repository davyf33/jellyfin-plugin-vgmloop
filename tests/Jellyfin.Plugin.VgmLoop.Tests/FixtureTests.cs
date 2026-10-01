using System;
using System.IO;
using System.Security.Cryptography;
using Jellyfin.Plugin.VgmLoop.Loop;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

/// <summary>
/// Real-world fixtures. The audio is copyrighted and not in the repo, so these tests skip unless the
/// files are found in $VGMLOOP_FIXTURES or ./fixtures.
/// </summary>
public class FixtureTests
{
    public static TheoryData<string, string, string, int, int, long, int?, long, long, long> Cases => new()
    {
        { "wii_menu.flac", "e9cf739411b3d7a89f7833ba655afdb6fecced56e768bdde7a4c3b05308520c7", "flac", 32000, 2, 2349639, null, 0, 197319, 2349639 },
        { "wii_menu_id3_prefix.flac", "ee475693dcf8f83dfa916eeabed0675e8ae33f23cfb4d2c72b37c1237cb2c7c8", "flac", 32000, 2, 2349639, null, 5822, 197319, 2349639 },
        { "wii_menu_vorbis.ogg", "c06f0c2747c8cda170963c4d9b7d003751bb65b21f6a136b55aeeeddc07341f8", "vorbis", 32000, 2, 2349639, null, 0, 197319, 2349639 },
        { "wii_menu_opus.opus", "28ca524a80c887a81161755071c437a7e0eb7abd7ec1a337702c57ebe2ab662e", "opus", 48000, 2, 3524459, 312, 0, 295979, 3524459 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture(string file, string sha256, string codec, int rate, int channels, long total, int? preSkip, long offset, long loopStart, long loopEnd)
    {
        var path = FindFixture(file);
        Assert.SkipWhen(path is null, $"{file} not available (set VGMLOOP_FIXTURES)");

        var bytes = File.ReadAllBytes(path!);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        using var ms = new MemoryStream(bytes);
        var h = HeaderParser.Parse(ms);
        Assert.Null(h.Error);
        Assert.Equal(codec, h.Codec);
        Assert.Equal(rate, h.SampleRate);
        Assert.Equal(channels, h.Channels);
        Assert.Equal(total, h.TotalSamples);
        Assert.Equal(preSkip, h.PreSkip);
        Assert.Equal(offset, h.AudioDataOffset);

        var loop = LoopInfoService.ResolveHeader(h);
        Assert.True(loop.HasLoop, loop.Reason);
        Assert.Equal(loopStart, loop.LoopStart);
        Assert.Equal(loopEnd, loop.LoopEnd);
    }

    private static string? FindFixture(string name)
    {
        var env = Environment.GetEnvironmentVariable("VGMLOOP_FIXTURES");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, name)))
        {
            return Path.Combine(env, name);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
