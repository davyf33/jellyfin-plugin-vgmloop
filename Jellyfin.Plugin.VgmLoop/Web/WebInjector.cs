using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.VgmLoop.Web;

/// <summary>
/// Pure text transforms applied to jellyfin-web's index.html and config.json.
/// </summary>
internal static class WebInjector
{
    /// <summary>
    /// The name of the window plugin added to config.json.
    /// </summary>
    public const string WindowPluginName = "VgmLoopPlayer";

    /// <summary>
    /// Builds the script tag that loads player.js.
    /// </summary>
    /// <param name="baseUrl">Normalized server base URL ("" or "/something").</param>
    /// <param name="version">Plugin version, used as a cache buster.</param>
    /// <returns>The script tag.</returns>
    public static string BuildScriptTag(string baseUrl, string version)
        => $"<script src=\"{baseUrl}/VgmLoop/web/player.js?v={Uri.EscapeDataString(version)}\"></script>";

    /// <summary>
    /// Inserts <paramref name="scriptTag"/> immediately before the first <c>&lt;/head&gt;</c>.
    /// </summary>
    /// <param name="html">Original index.html.</param>
    /// <param name="scriptTag">Tag to insert.</param>
    /// <param name="result">Rewritten html.</param>
    /// <returns><c>false</c> if there is no <c>&lt;/head&gt;</c>.</returns>
    public static bool TryInjectScript(string html, string scriptTag, out string result)
    {
        result = html;
        if (html.Contains("/VgmLoop/web/player.js", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var index = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        result = string.Concat(html.AsSpan(0, index), scriptTag, html.AsSpan(index));
        return true;
    }

    /// <summary>
    /// Appends <see cref="WindowPluginName"/> to the <c>plugins</c> array of config.json if absent.
    /// </summary>
    /// <param name="json">Original config.json.</param>
    /// <param name="result">Rewritten json.</param>
    /// <returns><c>false</c> if the json is invalid or has no <c>plugins</c> array.</returns>
    public static bool TryAddWindowPlugin(string json, out string result)
    {
        result = json;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject obj || obj["plugins"] is not JsonArray plugins)
        {
            return false;
        }

        foreach (var entry in plugins)
        {
            if (entry is JsonValue value && value.TryGetValue<string>(out var s) && s == WindowPluginName)
            {
                return true;
            }
        }

        plugins.Add(WindowPluginName);
        result = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return true;
    }

    /// <summary>
    /// Normalizes a Jellyfin BaseUrl setting to "" or "/segment" (no trailing slash).
    /// </summary>
    /// <param name="baseUrl">Configured base URL.</param>
    /// <returns>Normalized base URL.</returns>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().Trim('/');
        return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
    }
}
