using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.VgmLoop.Discs;
using Jellyfin.Plugin.VgmLoop.Loop;
using Jellyfin.Plugin.VgmLoop.Tags;
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
    /// <summary>Most ids one <c>LoopStatus</c> request checks (player.js sends batches of 100).</summary>
    public const int MaxLoopStatusItems = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly LoopInfoService _loopInfoService;
    private readonly TagService _tagService;
    private readonly ILogger<LoopInfoController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopInfoController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="authorizationContext">Authorization context.</param>
    /// <param name="loopInfoService">Loop info service.</param>
    /// <param name="tagService">Tag reader.</param>
    /// <param name="logger">Logger.</param>
    public LoopInfoController(
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        LoopInfoService loopInfoService,
        TagService tagService,
        ILogger<LoopInfoController> logger)
    {
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
        _loopInfoService = loopInfoService;
        _tagService = tagService;
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

        return new JsonResult(DiscsDto.From(album.Id, _tagService.GetDiscs(tracks)));
    }

    /// <summary>
    /// Tells which of the given tracks have a playable loop, for the loop icon in track lists.
    /// </summary>
    /// <param name="request">Track ids (at most <see cref="MaxLoopStatusItems"/>; the rest are ignored).</param>
    /// <returns>The ids of the tracks that loop. Ids the caller can't see, non-audio items and unreadable files are left out.</returns>
    [HttpPost("LoopStatus")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LoopStatusDto>> GetLoopStatus([FromBody] LoopStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var looping = new List<string>();
        if (Plugin.Instance?.Configuration.EnableLoopIcons == false || request.ItemIds is null)
        {
            return new JsonResult(new LoopStatusDto { LoopItemIds = looping });
        }

        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        var ids = request.ItemIds
            .Take(MaxLoopStatusItems)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length == 0 || (auth.User is null && !auth.IsApiKey))
        {
            return new JsonResult(new LoopStatusDto { LoopItemIds = looping });
        }

        // One query for the whole batch (it applies the user's library access), then the files in parallel: on a
        // network share each file costs a round trip or two, so reading them one by one is what makes this slow.
        var query = auth.User is not null ? new InternalItemsQuery(auth.User) : new InternalItemsQuery();
        query.ItemIds = ids;
        query.IncludeItemTypes = [BaseItemKind.Audio];
        var tracks = _libraryManager.GetItemList(query)
            .Where(t => t is AudioItem && !string.IsNullOrEmpty(t.Path))
            .ToList();

        var loops = new ConcurrentDictionary<Guid, bool>();
        Parallel.ForEach(tracks, new ParallelOptions { MaxDegreeOfParallelism = TagService.ReadParallelism }, track =>
        {
            try
            {
                if (_loopInfoService.Get(track.Path).Loop.HasLoop)
                {
                    loops[track.Id] = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "VGM Loop: could not read {Path}", track.Path);
            }
        });

        looping.AddRange(ids.Where(loops.ContainsKey).Select(id => id.ToString("N")));
        return new JsonResult(new LoopStatusDto { LoopItemIds = looping });
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
