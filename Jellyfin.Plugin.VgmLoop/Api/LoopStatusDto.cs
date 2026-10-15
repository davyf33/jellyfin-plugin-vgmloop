using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.VgmLoop.Api;

/// <summary>
/// Body of <c>POST /VgmLoop/LoopStatus</c>.
/// </summary>
public sealed class LoopStatusRequest
{
    /// <summary>Gets or sets the audio item ids to check (any <see cref="Guid"/> format; unparsable ones are skipped).</summary>
    [JsonPropertyName("itemIds")]
    public IReadOnlyList<string> ItemIds { get; set; } = [];
}

/// <summary>
/// Response of <c>POST /VgmLoop/LoopStatus</c>. Fixed camelCase names, read directly by player.js.
/// </summary>
public sealed class LoopStatusDto
{
    /// <summary>Gets or sets the ids (N format) of the requested tracks that have a playable loop.</summary>
    [JsonPropertyName("loopItemIds")]
    public IReadOnlyList<string> LoopItemIds { get; set; } = [];
}
