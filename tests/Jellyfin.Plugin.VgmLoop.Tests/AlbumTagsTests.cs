using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.VgmLoop.Loop;
using Jellyfin.Plugin.VgmLoop.Tags;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

public class AlbumTagsTests
{
    private static readonly AlbumTagField Platform = AlbumTagFields.Platform;
    private static readonly AlbumTagField Category = AlbumTagFields.Category;
    private static readonly AlbumTagField Series = AlbumTagFields.Series;

    private static Dictionary<string, string> Fields(params string[] kv)
        => kv.Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

    private static AlbumTagInput On(AlbumTagField field, params string[] values) => new(field, true, values);

    private static AlbumTagInput Off(AlbumTagField field) => new(field, false, []);

    /// <summary>Runs one scan: plan from the album's state, then apply it like the post-scan task does.</summary>
    private static (List<string> Tags, Dictionary<string, string> Ids, bool Changed) Scan(
        IReadOnlyList<string> tags, Dictionary<string, string> ids, params AlbumTagInput[] inputs)
    {
        var plan = AlbumTagMapper.Plan(false, tags, ids, inputs);
        var newIds = new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in plan.ProviderIds)
        {
            if (value is null)
            {
                newIds.Remove(key);
            }
            else
            {
                newIds[key] = value;
            }
        }

        return (plan.Tags.ToList(), newIds, plan.Changed);
    }

    private static Dictionary<string, string> NoIds() => new(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("PLATFORM=Nintendo 3DS", "platform", new[] { "Nintendo 3DS" })]
    [InlineData("console=Game Boy", "platform", new[] { "Game Boy" })]
    [InlineData("SYSTEM=PC", "platform", new[] { "PC" })]
    [InlineData("RELEASECATEGORY=Official Soundtrack", "category", new[] { "Official Soundtrack" })]
    [InlineData("SERIES=Animal Crossing", "series", new[] { "Animal Crossing" })]
    [InlineData("FRANCHISE=Super Mario", "series", new[] { "Super Mario" })]
    [InlineData("PLATFORM=Wii; Wii U|wii\u001FSwitch", "platform", new[] { "Wii", "Wii U", "Switch" })]
    [InlineData("PLATFORM=a;b;c;d;e;f;g", "platform", new[] { "a", "b", "c", "d", "e" })]
    [InlineData("PLATFORM=", "platform", new string[0])]
    [InlineData("GENRE=Soundtrack", "series", new string[0])]
    public void Read(string field, string name, string[] expected)
        => Assert.Equal(expected, AlbumTagMapper.Read(Fields(field), AlbumTagFields.All.Single(f => f.Name == name)));

    [Fact]
    public void Read_PrefersFirstKey()
    {
        Assert.Equal(["Nintendo 3DS"], AlbumTagMapper.Read(Fields("CONSOLE=3DS", "PLATFORM=Nintendo 3DS"), Platform));
        Assert.Equal(["Splatoon"], AlbumTagMapper.Read(Fields("FRANCHISE=Nintendo", "SERIES=Splatoon"), Series));
    }

    [Fact]
    public void ReadAll_ReadsEveryField()
    {
        var all = AlbumTagMapper.ReadAll(Fields("PLATFORM=Wii U", "RELEASECATEGORY=Game Rip", "SERIES=Splatoon", "TITLE=x"));
        Assert.Equal(["Wii U"], all[Platform.ProviderId]);
        Assert.Equal(["Game Rip"], all[Category.ProviderId]);
        Assert.Equal(["Splatoon"], all[Series.ProviderId]);
    }

    [Fact]
    public void FirstScan_AddsEveryFieldAndKeepsUserTags()
    {
        var (tags, ids, changed) = Scan(["Favourite"], NoIds(), On(Platform, "Nintendo 3DS"), On(Category, "Game Rip"), On(Series, "Animal Crossing"));
        Assert.True(changed);
        Assert.Equal(["Favourite", "Nintendo 3DS", "Game Rip", "Animal Crossing"], tags);
        Assert.Equal("Nintendo 3DS", ids["VgmLoopPlatform"]);
        Assert.Equal("Game Rip", ids["VgmLoopCategory"]);
        Assert.Equal("Animal Crossing", ids["VgmLoopSeries"]);
    }

    [Fact]
    public void ChangingCategory_SwapsTheTagOnTheNextScan()
    {
        var first = Scan([], NoIds(), On(Platform, "Nintendo 3DS"), On(Category, "Game Rip"), On(Series, "Animal Crossing"));
        var second = Scan(first.Tags, first.Ids, On(Platform, "Nintendo 3DS"), On(Category, "Official Soundtrack"), On(Series, "Animal Crossing"));
        Assert.True(second.Changed);
        Assert.Equal(["Nintendo 3DS", "Animal Crossing", "Official Soundtrack"], second.Tags);
        Assert.Equal("Official Soundtrack", second.Ids["VgmLoopCategory"]);
    }

    [Fact]
    public void ClearingSeries_RemovesOnlyTheSeriesTag()
    {
        var first = Scan(["Favourite"], NoIds(), On(Platform, "Wii"), On(Category, "Game Rip"), On(Series, "Super Mario"));
        var second = Scan(first.Tags, first.Ids, On(Platform, "Wii"), On(Category, "Game Rip"), On(Series));
        Assert.True(second.Changed);
        Assert.Equal(["Favourite", "Wii", "Game Rip"], second.Tags);
        Assert.False(second.Ids.ContainsKey("VgmLoopSeries"));
        Assert.Equal("Wii", second.Ids["VgmLoopPlatform"]);
    }

    [Fact]
    public void LockedAlbum_IsLeftAlone()
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["VgmLoopCategory"] = "Game Rip" };
        var plan = AlbumTagMapper.Plan(true, ["Game Rip", "Mine"], ids, [On(Platform, "Wii"), On(Category, "Official Soundtrack"), On(Series)]);
        Assert.False(plan.Changed);
        Assert.Equal(["Game Rip", "Mine"], plan.Tags);
        Assert.Empty(plan.ProviderIds);
    }

    [Fact]
    public void UnchangedAlbum_IsNotRewritten()
    {
        var first = Scan([], NoIds(), On(Platform, "Wii"), On(Category, "Game Rip"), On(Series, "Splatoon"));
        var second = Scan(first.Tags, first.Ids, On(Platform, "Wii"), On(Category, "Game Rip"), On(Series, "Splatoon"));
        Assert.False(second.Changed);
    }

    [Fact]
    public void TagTheUserAlreadyHad_IsNotClaimedAndSurvivesRemoval()
    {
        var first = Scan(["Splatoon"], NoIds(), On(Platform), On(Category), On(Series, "Splatoon"));
        Assert.False(first.Changed); // nothing to add, and the tag stays the user's
        Assert.False(first.Ids.ContainsKey("VgmLoopSeries"));

        var second = Scan(first.Tags, first.Ids, On(Platform), On(Category), On(Series));
        Assert.Equal(["Splatoon"], second.Tags);
    }

    [Fact]
    public void ValueSharedByTwoFields_StaysUntilNeitherWantsIt()
    {
        // e.g. a series and a platform both called "Game Boy" (silly, but the rule must hold).
        var first = Scan([], NoIds(), On(Platform, "Game Boy"), On(Category), On(Series, "Game Boy"));
        Assert.Equal(["Game Boy"], first.Tags);

        var second = Scan(first.Tags, first.Ids, On(Platform, "Game Boy"), On(Category), On(Series));
        Assert.Equal(["Game Boy"], second.Tags);

        var third = Scan(second.Tags, second.Ids, On(Platform), On(Category), On(Series));
        Assert.Empty(third.Tags);
    }

    [Fact]
    public void DisabledField_KeepsItsTags_AndOthersCantRemoveThem()
    {
        var first = Scan([], NoIds(), On(Platform, "Wii"), On(Category, "Game Rip"), On(Series, "Wii"));
        var second = Scan(first.Tags, first.Ids, Off(Platform), On(Category, "Game Rip"), On(Series));
        Assert.Equal(["Wii", "Game Rip"], second.Tags);
        Assert.Equal("Wii", second.Ids["VgmLoopPlatform"]);
    }

    [Fact]
    public void PlatformRecordFromVersion11_StillWorks()
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["VgmLoopPlatform"] = "3DS" };
        var (tags, newIds, changed) = Scan(["3DS", "Mine"], ids, On(Platform, "Nintendo 3DS"), On(Category), On(Series));
        Assert.True(changed);
        Assert.Equal(["Mine", "Nintendo 3DS"], tags);
        Assert.Equal("Nintendo 3DS", newIds["VgmLoopPlatform"]);
    }

    [Fact]
    public void Applied_RoundTrips()
    {
        Assert.Null(AlbumTagMapper.FormatApplied([]));
        Assert.Empty(AlbumTagMapper.ParseApplied(null));
        Assert.Equal(["Wii", "Wii U"], AlbumTagMapper.ParseApplied(AlbumTagMapper.FormatApplied(["Wii", "Wii U"])));
    }

    [Fact]
    public void Service_ReadsFieldsFromFlacAndMp3()
    {
        var flac = DiscNamesTests.WriteTemp(".flac", Synth.Flac(44100, 2, 16, 441000, ["PLATFORM=Nintendo 3DS", "RELEASECATEGORY=Game Rip", "SERIES=Animal Crossing", "DISCSUBTITLE=OST"]));
        var mp3 = DiscNamesTests.WriteTemp(".mp3", Synth.Mp3WithId3Frames(("TIT2", "Field"), ("TXXX:PLATFORM", "Wii"), ("TXXX:RELEASECATEGORY", "Official Soundtrack"), ("TXXX:FRANCHISE", "Super Mario")));
        try
        {
            var service = new TagService(new LoopInfoService(NullLogger<LoopInfoService>.Instance), NullLogger<TagService>.Instance);
            var tags = service.Get(flac);
            Assert.Equal("OST", tags.DiscName);
            Assert.Equal(["Nintendo 3DS"], tags.Values(Platform));
            Assert.Equal(["Game Rip"], tags.Values(Category));
            Assert.Equal(["Animal Crossing"], tags.Values(Series));
            Assert.Equal(["Wii"], service.GetValues(mp3, Platform));
            Assert.Equal(["Official Soundtrack"], service.GetValues(mp3, Category));
            Assert.Equal(["Super Mario"], service.GetValues(mp3, Series));
        }
        finally
        {
            File.Delete(flac);
            File.Delete(mp3);
        }
    }
}
