using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.VgmLoop.Loop;
using Jellyfin.Plugin.VgmLoop.Platforms;
using Jellyfin.Plugin.VgmLoop.Tags;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

public class PlatformTagsTests
{
    private static Dictionary<string, string> Fields(params string[] kv)
        => kv.Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

    [Theory]
    [InlineData("PLATFORM=Nintendo 3DS", new[] { "Nintendo 3DS" })]
    [InlineData("platform=  Wii  ", new[] { "Wii" })]
    [InlineData("CONSOLE=Game Boy", new[] { "Game Boy" })]
    [InlineData("SYSTEM=PC", new[] { "PC" })]
    [InlineData("PLATFORM=Wii; Wii U", new[] { "Wii", "Wii U" })]
    [InlineData("PLATFORM=Wii\u001FWii U\u001Fwii", new[] { "Wii", "Wii U" })]
    [InlineData("PLATFORM=", new string[0])]
    [InlineData("GENRE=Soundtrack", new string[0])]
    public void FromFields(string field, string[] expected)
        => Assert.Equal(expected, PlatformTags.FromFields(Fields(field)));

    [Fact]
    public void FromFields_PrefersPlatformOverConsole()
        => Assert.Equal(["Nintendo 3DS"], PlatformTags.FromFields(Fields("CONSOLE=3DS", "PLATFORM=Nintendo 3DS")));

    [Fact]
    public void Apply_AddsPlatformAndKeepsOtherTags()
    {
        var (tags, changed) = PlatformTags.Apply(["Favourite"], [], ["Nintendo 3DS"]);
        Assert.True(changed);
        Assert.Equal(["Favourite", "Nintendo 3DS"], tags);
    }

    [Fact]
    public void Apply_ReplacesPlatformItAddedBefore()
    {
        var (tags, changed) = PlatformTags.Apply(["3DS", "Favourite"], ["3DS"], ["Nintendo 3DS"]);
        Assert.True(changed);
        Assert.Equal(["Favourite", "Nintendo 3DS"], tags);
    }

    [Fact]
    public void Apply_RemovesItsTagWhenFilesNoLongerSayIt()
    {
        var (tags, changed) = PlatformTags.Apply(["Wii", "Favourite"], ["Wii"], []);
        Assert.True(changed);
        Assert.Equal(["Favourite"], tags);
    }

    [Fact]
    public void Apply_LeavesUserTagsAlone()
    {
        // "GameCube" was added by hand, not by the plugin, so it stays even though the files don't say it.
        var (tags, changed) = PlatformTags.Apply(["GameCube", "Wii"], ["Wii"], ["Wii"]);
        Assert.False(changed);
        Assert.Equal(["GameCube", "Wii"], tags);
    }

    [Fact]
    public void Apply_NoDuplicateWhenUserAlreadyHasTheTag()
    {
        var (tags, changed) = PlatformTags.Apply(["nintendo 3ds"], [], ["Nintendo 3DS"]);
        Assert.False(changed);
        Assert.Equal(["nintendo 3ds"], tags);
    }

    [Fact]
    public void Applied_RoundTrips()
    {
        Assert.Null(PlatformTags.FormatApplied([]));
        Assert.Empty(PlatformTags.ParseApplied(null));
        Assert.Equal(["Wii", "Wii U"], PlatformTags.ParseApplied(PlatformTags.FormatApplied(["Wii", "Wii U"])));
    }

    [Fact]
    public void Service_ReadsPlatformFromFlacAndMp3()
    {
        var flac = DiscNamesTests.WriteTemp(".flac", Synth.Flac(44100, 2, 16, 441000, ["TITLE=Field", "PLATFORM=Nintendo 3DS", "DISCSUBTITLE=OST"]));
        var mp3 = DiscNamesTests.WriteTemp(".mp3", Synth.Mp3WithId3Frames(("TIT2", "Field"), ("TXXX:PLATFORM", "Wii")));
        try
        {
            var service = new TagService(new LoopInfoService(NullLogger<LoopInfoService>.Instance), NullLogger<TagService>.Instance);
            Assert.Equal(new FileTags("OST", ["Nintendo 3DS"]), service.Get(flac), new FileTagsComparer());
            Assert.Equal(["Wii"], service.GetPlatforms(mp3));
        }
        finally
        {
            File.Delete(flac);
            File.Delete(mp3);
        }
    }

    private sealed class FileTagsComparer : IEqualityComparer<FileTags>
    {
        public bool Equals(FileTags? x, FileTags? y)
            => x is not null && y is not null && x.DiscName == y.DiscName && x.Platforms.SequenceEqual(y.Platforms);

        public int GetHashCode(FileTags obj) => 0;
    }
}
