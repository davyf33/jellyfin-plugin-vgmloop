using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VgmLoop.Web;

/// <summary>
/// Rewrites jellyfin-web's index.html and config.json so the VGM Loop window plugin gets loaded.
/// Any failure passes the request through untouched.
/// </summary>
public class WebInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IConfiguration _appConfiguration;
    private readonly ILogger<WebInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="applicationPaths">Application paths (for the web directory).</param>
    /// <param name="configurationManager">Server configuration (for BaseUrl).</param>
    /// <param name="appConfiguration">App configuration (for hostwebclient).</param>
    /// <param name="logger">Logger.</param>
    public WebInjectionMiddleware(
        RequestDelegate next,
        IApplicationPaths applicationPaths,
        IServerConfigurationManager configurationManager,
        IConfiguration appConfiguration,
        ILogger<WebInjectionMiddleware> logger)
    {
        _next = next;
        _applicationPaths = applicationPaths;
        _configurationManager = configurationManager;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || Plugin.Instance?.Configuration.EnablePlayer != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var target = Classify(context.Request);
        if (target == Target.None || !_appConfiguration.HostWebClient())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? body = null;
        try
        {
            body = target == Target.Index ? RewriteIndex() : RewriteConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VGM Loop: failed to rewrite {Path}; serving it unmodified", context.Request.Path);
        }

        if (body is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = target == Target.Index ? "text/html; charset=utf-8" : "application/json; charset=utf-8";
        response.ContentLength = bytes.Length;
        response.Headers.CacheControl = "no-cache";
        await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private Target Classify(HttpRequest request)
    {
        var path = (request.PathBase + request.Path).Value ?? string.Empty;
        var web = BaseUrl + "/web/";
        if (path.Equals(web, StringComparison.OrdinalIgnoreCase)
            || path.Equals(web + "index.html", StringComparison.OrdinalIgnoreCase))
        {
            return Target.Index;
        }

        if (path.Equals(web + "config.json", StringComparison.OrdinalIgnoreCase))
        {
            return Target.Config;
        }

        return Target.None;
    }

    private string BaseUrl => WebInjector.NormalizeBaseUrl(_configurationManager.GetNetworkConfiguration().BaseUrl);

    private string? RewriteIndex()
    {
        var html = ReadWebFile("index.html");
        if (html is null)
        {
            return null;
        }

        var version = Plugin.Instance?.Version.ToString() ?? "0";
        if (!WebInjector.TryInjectScript(html, WebInjector.BuildScriptTag(BaseUrl, version), out var result))
        {
            _logger.LogWarning("VGM Loop: no </head> in index.html; serving it unmodified");
            return null;
        }

        return result;
    }

    private string? RewriteConfig()
    {
        var json = ReadWebFile("config.json");
        if (json is null)
        {
            return null;
        }

        if (!WebInjector.TryAddWindowPlugin(json, out var result))
        {
            _logger.LogWarning("VGM Loop: config.json is not valid JSON with a plugins array; serving it unmodified");
            return null;
        }

        return result;
    }

    private string? ReadWebFile(string name)
    {
        var path = Path.Combine(_applicationPaths.WebPath, name);
        if (!File.Exists(path))
        {
            _logger.LogWarning("VGM Loop: {Path} not found; serving the original response", path);
            return null;
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }

    private enum Target
    {
        None,
        Index,
        Config
    }
}
