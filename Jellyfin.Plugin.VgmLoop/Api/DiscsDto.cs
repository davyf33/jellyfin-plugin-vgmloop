using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.VgmLoop.Discs;

namespace Jellyfin.Plugin.VgmLoop.Api;

/// <summary>
/// Response of <c>GET /VgmLoop/Items/{itemId}/Discs</c>. Fixed camelCase names, read directly by player.js.
/// </summary>
public sealed class DiscsDto
{
    /// <summary>Gets or sets the album id (N format).</summary>
    [JsonPropertyName("albumId")]
    public string AlbumId { get; set; } = string.Empty;

    /// <summary>Gets or sets the named discs. Discs without a name are not listed.</summary>
    [JsonPropertyName("discs")]
    public IReadOnlyList<DiscDto> Discs { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the page should add Play and Shuffle buttons to disc headers.</summary>
    [JsonPropertyName("discButtons")]
    public bool DiscButtons { get; set; }

    /// <summary>
    /// Builds the DTO.
    /// </summary>
    /// <param name="albumId">Album id.</param>
    /// <param name="discs">Named discs.</param>
    /// <param name="discButtons">Whether disc headers get Play and Shuffle buttons.</param>
    /// <returns>The DTO.</returns>
    public static DiscsDto From(Guid albumId, IEnumerable<DiscName> discs, bool discButtons)
    {
        ArgumentNullException.ThrowIfNull(discs);
        return new DiscsDto
        {
            AlbumId = albumId.ToString("N"),
            DiscButtons = discButtons,
            Discs = discs.Select(d => new DiscDto
            {
                DiscNumber = d.DiscNumber,
                Name = d.Name,
                ItemIds = d.ItemIds.Select(id => id.ToString("N")).ToList()
            }).ToList()
        };
    }
}

/// <summary>
/// One named disc.
/// </summary>
public sealed class DiscDto
{
    /// <summary>Gets or sets the disc number.</summary>
    [JsonPropertyName("discNumber")]
    public int DiscNumber { get; set; }

    /// <summary>Gets or sets the disc name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the ids (N format) of the disc's tracks.</summary>
    [JsonPropertyName("itemIds")]
    public IReadOnlyList<string> ItemIds { get; set; } = [];
}
