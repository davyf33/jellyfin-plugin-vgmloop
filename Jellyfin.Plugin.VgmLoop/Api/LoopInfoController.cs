using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.VgmLoop.Discs;
using Jellyfin.Plugin.VgmLoop.Loop;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.VgmLoop.Api;

/// <summary>
/// Loop metadata and disc names for audio items.
/// </summary>
[ApiController]
[Authorize]
[Route("VgmLoop")]
public class LoopInfoController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly LoopInfoService _loopInfoService;
    private readonly DiscInfoService _discInfoService;
    private readonly ILogger<LoopInfoController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopInfoController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="authorizationContext">Authorization context.</param>
    /// <param name="loopInfoService">Loop info service.</param>
    /// <param name="discInfoService">Disc info service.</param>
    /// <param name="logger">Logger.</param>
    public LoopInfoController(
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        LoopInfoService loopInfoService,
        DiscInfoService discInfoService,
        ILogger<LoopInfoController> logger)
    {
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
        _loopInfoService = loopInfoService;
        _discInfoService = discInfoService;
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
        var (item, _) = await GetVisibleItem(itemId).ConfigureAwait(false);
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

    /// <summary>
    /// Gets the disc names (disc subtitles) tagged in an album's files, e.g. <c>DISCSUBTITLE</c> or ID3 <c>TSST</c>.
    /// </summary>
    /// <param name="itemId">Album id, or the id of one of its tracks.</param>
    /// <returns>The album id and its named discs; the list is empty when no disc has a name.</returns>
    [HttpGet("Items/{itemId}/Discs")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscsDto>> GetDiscs([FromRoute] Guid itemId)
    {
        var (item, user) = await GetVisibleItem(itemId).ConfigureAwait(false);
        if (item is null)
        {
            return NotFound();
        }

        var album = item as MusicAlbum ?? (item as AudioItem)?.AlbumEntity;
        if (album is null)
        {
            return BadRequest("Not a music album or track.");
        }

        if (Plugin.Instance?.Configuration.EnableDiscNames == false)
        {
            return new JsonResult(DiscsDto.From(album.Id, []));
        }

        IEnumerable<BaseItem> children = user is not null
            ? _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ParentId = album.Id,
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            })
            : album.Tracks;

        var tracks = children
            .OfType<AudioItem>()
            .Where(t => !string.IsNullOrEmpty(t.Path))
            .Select(t => new DiscTrack(t.Id, t.ParentIndexNumber, t.IndexNumber, t.Path))
            .ToList();

        return new JsonResult(DiscsDto.From(album.Id, _discInfoService.GetDiscs(tracks)));
    }

    /// <summary>
    /// Looks up an item the caller may see: user tokens see that user's items, API keys (admin-equivalent, no user)
    /// see everything.
    /// </summary>
    private async Task<(BaseItem? Item, User? User)> GetVisibleItem(Guid itemId)
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        BaseItem? item = auth.User is not null
            ? _libraryManager.GetItemById<BaseItem>(itemId, auth.User)
            : auth.IsApiKey ? _libraryManager.GetItemById(itemId) : null;
        return (item, auth.User);
    }
}
