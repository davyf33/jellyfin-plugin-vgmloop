using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VgmLoop.Tags;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VgmLoop.Platforms;

/// <summary>
/// After every library scan, gives each music album a Jellyfin tag per platform found in its files' PLATFORM tag
/// (e.g. "Nintendo 3DS"), so the music library can be filtered by platform under Filter → Tags. Albums whose Tags field
/// is locked are left alone. Jellyfin finds this class by itself (it scans plugin assemblies for post-scan tasks).
/// </summary>
public class PlatformTagsPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly TagService _tagService;
    private readonly ILogger<PlatformTagsPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformTagsPostScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="tagService">Tag reader.</param>
    /// <param name="logger">Logger.</param>
    public PlatformTagsPostScanTask(ILibraryManager libraryManager, TagService tagService, ILogger<PlatformTagsPostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _tagService = tagService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Plugin.Instance?.Configuration.EnablePlatformTags == false)
        {
            progress.Report(100);
            return;
        }

        var albums = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true
        }).OfType<MusicAlbum>().ToList();

        var updated = 0;
        for (var i = 0; i < albums.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await UpdateAlbum(albums[i], cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One bad album must not stop the rest.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "VGM Loop: could not update platform tags of {Album}", albums[i].Name);
            }

            progress.Report(100.0 * (i + 1) / albums.Count);
        }

        if (updated > 0)
        {
            _logger.LogInformation("VGM Loop: updated platform tags on {Count} albums", updated);
        }
    }

    private async Task<bool> UpdateAlbum(MusicAlbum album, CancellationToken cancellationToken)
    {
        if (album.LockedFields.Contains(MetadataField.Tags))
        {
            return false;
        }

        var platforms = ReadPlatforms(album);
        album.ProviderIds.TryGetValue(PlatformTags.AppliedKey, out var appliedValue);
        var applied = PlatformTags.ParseApplied(appliedValue);
        var (tags, changed) = PlatformTags.Apply(album.Tags ?? [], applied, platforms);
        var newApplied = PlatformTags.FormatApplied(platforms);
        if (!changed && string.Equals(appliedValue, newApplied, StringComparison.Ordinal))
        {
            return false;
        }

        album.Tags = tags;
        if (newApplied is null)
        {
            album.ProviderIds.Remove(PlatformTags.AppliedKey);
        }
        else
        {
            album.ProviderIds[PlatformTags.AppliedKey] = newApplied;
        }

        await _libraryManager.UpdateItemAsync(album, album.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private IReadOnlyList<string> ReadPlatforms(MusicAlbum album)
    {
        var tracks = album.Tracks
            .Where(t => !string.IsNullOrEmpty(t.Path))
            .OrderBy(t => t.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(t => t.IndexNumber ?? int.MaxValue)
            .ThenBy(t => t.Path, StringComparer.Ordinal)
            .Take(PlatformTags.MaxTracksRead);
        foreach (var track in tracks)
        {
            var platforms = _tagService.GetPlatforms(track.Path);
            if (platforms.Count > 0)
            {
                return platforms;
            }
        }

        return [];
    }
}
