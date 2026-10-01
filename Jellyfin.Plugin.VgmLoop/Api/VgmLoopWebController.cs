using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VgmLoop.Api;

/// <summary>
/// Serves the embedded client script. Anonymous: a script tag can't send auth headers and loads before login.
/// </summary>
[ApiController]
[Route("VgmLoop/web")]
[AllowAnonymous]
public class VgmLoopWebController : ControllerBase
{
    private const string PlayerResource = "Jellyfin.Plugin.VgmLoop.Web.player.js";

    /// <summary>
    /// Gets the player script.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("player.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetPlayerScript()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PlayerResource);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript; charset=utf-8");
    }
}
