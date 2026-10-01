using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.VgmLoop.Loop;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.VgmLoop.Api;

/// <summary>
/// Loop metadata for audio items.
/// </summary>
[ApiController]
[Authorize]
[Route("VgmLoop")]
public class LoopInfoController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly LoopInfoService _loopInfoService;
    private readonly ILogger<LoopInfoController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopInfoController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="authorizationContext">Authorization context.</param>
    /// <param name="loopInfoService">Loop info service.</param>
    /// <param name="logger">Logger.</param>
    public LoopInfoController(
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        LoopInfoService loopInfoService,
        ILogger<LoopInfoController> logger)
    {
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
        _loopInfoService = loopInfoService;
        _logger = logger;
    }

    /// <summary>
    /// Gets loop metadata for an audio item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Loop metadata; <c>hasLoop</c> is false with a <c>reason</c> when the track can't loop.</returns>
    [HttpGet("Items/{itemId}/LoopInfo")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoopInfoDto>> GetLoopInfo([FromRoute] Guid itemId)
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);

        // User tokens only see items that user can see. API keys (admin-equivalent, no user) see everything.
        BaseItem? item = auth.User is not null
            ? _libraryManager.GetItemById<BaseItem>(itemId, auth.User)
            : auth.IsApiKey ? _libraryManager.GetItemById(itemId) : null;

        if (item is null)
        {
            return NotFound();
        }

        if (item is not AudioItem || string.IsNullOrEmpty(item.Path))
        {
            return BadRequest("Not an audio item.");
        }

        FileLoopInfo info;
        try
        {
            info = _loopInfoService.Get(item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "VGM Loop: could not read {Path}", item.Path);
            return NotFound();
        }

        return new JsonResult(LoopInfoDto.From(item.Id, info));
    }
}
