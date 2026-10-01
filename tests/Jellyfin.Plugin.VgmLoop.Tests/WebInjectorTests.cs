using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.VgmLoop.Web;
using Xunit;

namespace Jellyfin.Plugin.VgmLoop.Tests;

public class WebInjectorTests
{
    private const string Tag = "<script src=\"/VgmLoop/web/player.js?v=0.1.0.0\"></script>";

    private static string TestData(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    [InlineData(" /a/b/ ", "/a/b")]
    public void NormalizeBaseUrl(string? input, string expected)
        => Assert.Equal(expected, WebInjector.NormalizeBaseUrl(input));

    [Fact]
    public void BuildScriptTag_UsesBaseUrl()
        => Assert.Equal(
            "<script src=\"/jf/VgmLoop/web/player.js?v=1.2.3.4\"></script>",
            WebInjector.BuildScriptTag("/jf", "1.2.3.4"));

    [Fact]
    public void InjectScript_InsertsBeforeHeadClose()
    {
        Assert.True(WebInjector.TryInjectScript("<html><head><title>x</title></HEAD><body></body></html>", Tag, out var result));
        Assert.Equal($"<html><head><title>x</title>{Tag}</HEAD><body></body></html>", result);
    }

    [Fact]
    public void InjectScript_NoHead_Fails()
    {
        Assert.False(WebInjector.TryInjectScript("<html><body></body></html>", Tag, out var result));
        Assert.Equal("<html><body></body></html>", result);
    }

    [Fact]
    public void InjectScript_Idempotent()
    {
        Assert.True(WebInjector.TryInjectScript("<head></head>", Tag, out var once));
        Assert.True(WebInjector.TryInjectScript(once, Tag, out var twice));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void InjectScript_Live1210Index_RunsBeforeDeferredBundles()
    {
        var html = TestData("index-12.1.0.html");
        Assert.True(WebInjector.TryInjectScript(html, Tag, out var result));

        var tagAt = result.IndexOf(Tag, StringComparison.Ordinal);
        Assert.True(tagAt > 0);
        Assert.Equal(tagAt + Tag.Length, result.IndexOf("</head>", StringComparison.Ordinal));

        // Our tag is a classic (parser-blocking) script; it runs before any deferred script
        // as long as jellyfin-web's own scripts are all deferred and none are async/inline.
        var jfScripts = System.Text.RegularExpressions.Regex.Matches(html, "<script[^>]*>").Select(m => m.Value).ToList();
        Assert.NotEmpty(jfScripts);
        Assert.All(jfScripts, s => Assert.Contains("defer", s, StringComparison.Ordinal));
    }

    [Fact]
    public void AddWindowPlugin_AppendsOnce()
    {
        const string json = "{\"multiserver\":false,\"plugins\":[\"htmlAudioPlayer/plugin\",\"syncPlay/plugin\"]}";
        Assert.True(WebInjector.TryAddWindowPlugin(json, out var once));
        Assert.Equal(
            new[] { "htmlAudioPlayer/plugin", "syncPlay/plugin", "VgmLoopPlayer" },
            Plugins(once));

        Assert.True(WebInjector.TryAddWindowPlugin(once, out var twice));
        Assert.Equal(Plugins(once), Plugins(twice));
    }

    [Fact]
    public void AddWindowPlugin_Live1210Config_PreservesEverythingElse()
    {
        var json = TestData("config-12.1.0.json");
        Assert.True(WebInjector.TryAddWindowPlugin(json, out var result));

        var before = Plugins(json);
        var after = Plugins(result);
        Assert.Equal(15, before.Length);
        Assert.Equal([.. before, "VgmLoopPlayer"], after);

        using var a = JsonDocument.Parse(json);
        using var b = JsonDocument.Parse(result);
        foreach (var prop in a.RootElement.EnumerateObject().Where(p => p.Name != "plugins"))
        {
            Assert.Equal(prop.Value.GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal),
                b.RootElement.GetProperty(prop.Name).GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"plugins\":\"x\"}")]
    [InlineData("{}")]
    public void AddWindowPlugin_Invalid_Fails(string json)
    {
        Assert.False(WebInjector.TryAddWindowPlugin(json, out var result));
        Assert.Equal(json, result);
    }

    private static string[] Plugins(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("plugins").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
}
