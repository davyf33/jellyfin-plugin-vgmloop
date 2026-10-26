using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.VgmLoop.Discs;
using Jellyfin.Plugin.VgmLoop.Loop;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VgmLoop.Tags;

/// <summary>
/// What the plugin uses from one file's tags.
/// </summary>
/// <param name="DiscName">Disc name (subtitle), or <c>null</c>.</param>
/// <param name="Fields">Album tag field values by <see cref="AlbumTagField.ProviderId"/>; fields without values are absent.</param>
public sealed record FileTags(string? DiscName, IReadOnlyDictionary<string, IReadOnlyList<string>> Fields)
{
    /// <summary>Gets the value for a file without usable tags.</summary>
    public static FileTags Empty { get; } = new(null, new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>
    /// Builds the value from a file's tag fields.
    /// </summary>
    /// <param name="fields">Tag fields (key → value), any key casing.</param>
    /// <returns>The value.</returns>
    public static FileTags From(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var list = fields as IReadOnlyCollection<KeyValuePair<string, string>> ?? fields.ToList();
        return new FileTags(DiscNames.FromFields(list), AlbumTagMapper.ReadAll(list));
    }

    /// <summary>
    /// Gets one field's values.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The values; empty when the file has none.</returns>
    public IReadOnlyList<string> Values(AlbumTagField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Fields.TryGetValue(field.ProviderId, out var values) ? values : [];
    }
}

/// <summary>
/// Reads disc names and album tag fields (platform, category, series) from audio tags. FLAC and Ogg come from the loop header parse (one cached read
/// serves the loop icon, disc names and platforms); other formats are read with ATL, the tag library Jellyfin itself
/// uses, so MP3, M4A and the rest are covered. Cached by path + size + mtime.
/// </summary>
public class TagService
{
    /// <summary>Files read at once when an album's discs are looked up (network shares are slow per file, not per byte).</summary>
    public const int ReadParallelism = 8;

    private const int MaxEntries = 50_000;

    private static readonly string[] HeaderParsedExtensions = [".flac", ".ogg", ".oga", ".opus"];

    private readonly FileCache<FileTags> _cache = new(MaxEntries);
    private readonly LoopInfoService _loopInfoService;
    private readonly ILogger<TagService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagService"/> class.
    /// </summary>
    /// <param name="loopInfoService">Loop header reader (FLAC and Ogg tags come from it).</param>
    /// <param name="logger">Logger.</param>
    public TagService(LoopInfoService loopInfoService, ILogger<TagService> logger)
    {
        _loopInfoService = loopInfoService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the named discs of an album. The first track of every disc is read in parallel up front, since that is
    /// normally the only one needed.
    /// </summary>
    /// <param name="tracks">The album's tracks.</param>
    /// <returns>Named discs, by disc number.</returns>
    public IReadOnlyList<DiscName> GetDiscs(IEnumerable<DiscTrack> tracks)
    {
        var list = tracks.ToList();
        var firstPerDisc = list
            .Where(t => t.DiscNumber is not null)
            .GroupBy(t => t.DiscNumber)
            .Select(g => g.OrderBy(t => t.TrackNumber ?? int.MaxValue).ThenBy(t => t.Path, StringComparer.Ordinal).First())
            .ToList();
        Parallel.ForEach(firstPerDisc, new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism }, t => Get(t.Path));
        return DiscNames.Build(list, t => GetDiscName(t.Path));
    }

    /// <summary>
    /// Gets the disc name tagged in one file.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The name, or <c>null</c> when the file has none or can't be read.</returns>
    public string? GetDiscName(string path) => Get(path).DiscName;

    /// <summary>
    /// Gets one album tag field's values tagged in one file.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="field">The field.</param>
    /// <returns>The values; empty when the file has none or can't be read.</returns>
    public IReadOnlyList<string> GetValues(string path, AlbumTagField field) => Get(path).Values(field);

    /// <summary>
    /// Gets the tags the plugin uses from one file.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The tags; <see cref="FileTags.Empty"/> when the file can't be read.</returns>
    public FileTags Get(string path)
    {
        try
        {
            if (HeaderParsedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                var info = _loopInfoService.Get(path);
                if (info.Header.Codec != "unknown")
                {
                    return info.Tags;
                }
            }

            var file = new FileInfo(path);
            return file.Exists ? _cache.GetOrRead(file, (f, _) => ReadWithAtl(f.FullName)) : FileTags.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug(ex, "VGM Loop: could not read {Path}", path);
            return FileTags.Empty;
        }
    }

    private FileTags ReadWithAtl(string path)
    {
        try
        {
            var fields = new ATL.Track(path).AdditionalFields;
            return FileTags.From(fields);
        }
#pragma warning disable CA1031 // A tag we can't read just means no value; never fail the caller over it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "VGM Loop: could not read tags of {Path}", path);
            return FileTags.Empty;
        }
    }
}
