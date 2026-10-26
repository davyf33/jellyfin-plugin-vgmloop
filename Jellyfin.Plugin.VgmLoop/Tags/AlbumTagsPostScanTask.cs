using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VgmLoop.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VgmLoop.Tags;

/// <summary>
/// After every library scan, turns file tags into Jellyfin tags on each music album, one field at a time (see
/// <see cref="AlbumTagFields"/>): <c>PLATFORM</c>, <c>RELEASECATEGORY</c> and <c>SERIES</c>. The music library can then
/// be filtered by them under Filter → Tags. Albums whose Tags field is locked are left alone. Jellyfin finds this class
/// by itself (it scans plugin assemblies for post-scan tasks).
/// </summary>
public class AlbumTagsPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly TagService _tagService;
    private readonly ILogger<AlbumTagsPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlbumTagsPostScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="tagService">Tag reader.</param>
    /// <param name="logger">Logger.</param>
    public AlbumTagsPostScanTask(ILibraryManager libraryManager, TagService tagService, ILogger<AlbumTagsPostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _tagService = tagService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var enabled = AlbumTagFields.All.Select(f => (Field: f, Enabled: config.IsEnabled(f.Setting))).ToList();
        if (!enabled.Any(e => e.Enabled))
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
                if (await UpdateAlbum(albums[i], enabled, cancellationToken).ConfigureAwait(false))
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
                _logger.LogWarning(ex, "VGM Loop: could not update the tags of {Album}", albums[i].Name);
            }

            progress.Report(100.0 * (i + 1) / albums.Count);
        }

        if (updated > 0)
        {
            _logger.LogInformation("VGM Loop: updated tags on {Count} albums", updated);
        }
    }

    private async Task<bool> UpdateAlbum(MusicAlbum album, IReadOnlyList<(AlbumTagField Field, bool Enabled)> fields, CancellationToken cancellationToken)
    {
        if (album.LockedFields.Contains(MetadataField.Tags))
        {
            return false;
        }

        var trackTags = album.Tracks
            .Where(t => !string.IsNullOrEmpty(t.Path))
            .OrderBy(t => t.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(t => t.IndexNumber ?? int.MaxValue)
            .ThenBy(t => t.Path, StringComparer.Ordinal)
            .Take(AlbumTagMapper.MaxTracksRead)
            .Select(t => _tagService.Get(t.Path))
            .ToList();

        // Each field comes from the first of those tracks that has it.
        var inputs = fields
            .Select(f => new AlbumTagInput(
                f.Field,
                f.Enabled,
                f.Enabled ? trackTags.Select(t => t.Values(f.Field)).FirstOrDefault(v => v.Count > 0) ?? [] : []))
            .ToList();

        var plan = AlbumTagMapper.Plan(false, album.Tags ?? [], album.ProviderIds, inputs);
        if (!plan.Changed)
        {
            return false;
        }

        album.Tags = plan.Tags;
        foreach (var (key, value) in plan.ProviderIds)
        {
            if (value is null)
            {
                album.ProviderIds.Remove(key);
            }
            else
            {
                album.ProviderIds[key] = value;
            }
        }

        await _libraryManager.UpdateItemAsync(album, album.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
