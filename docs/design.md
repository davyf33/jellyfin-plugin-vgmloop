# Design

VGM Loop is one Jellyfin server plugin with two halves: a small API that reads loop tags, and a replacement audio
player injected into jellyfin-web at runtime. Neither Jellyfin nor jellyfin-web is forked, and files on disk are never
modified.

## Server

**Web injection** (`Web/`). An `IStartupFilter` puts a middleware in front of Jellyfin's pipeline. For `GET
{BaseUrl}/web/`, `/web/index.html` and `/web/config.json` it reads the original file from the web directory and:

- inserts `<script src="{BaseUrl}/VgmLoop/web/player.js?v={version}">` immediately before `</head>`;
- appends `"VgmLoopPlayer"` to the `plugins` array of `config.json`.

Any failure (missing file, bad JSON, no `</head>`) logs a warning and passes the request through untouched. The
plugin setting "Enable VGM Loop player" turns the rewriting off.

**Loop metadata** (`Loop/`). A self-contained header parser reads only what it needs from the start of the file plus,
for Ogg, the last 64 KB (256 KB if the last page isn't in there):

- skips a leading ID3v2 tag and records its length as `audioDataOffset`;
- FLAC: STREAMINFO (rate, channels, bits, total samples) and VORBIS_COMMENT;
- Ogg: reassembles the first logical stream's identification and comment packets (which may span pages, e.g. with
  large cover art); Opus or Vorbis parameters; total samples from the last page's granule position (Opus: minus
  pre-skip).

`LoopResolver` turns `LOOPSTART`/`LOOP_START`, `LOOPLENGTH`/`LOOP_LENGTH` and `LOOPEND`/`LOOP_END` into an exclusive
`[loopStart, loopEnd)` range in native-rate samples. Values may be samples, decimal seconds, `m:ss(.fff)` or
`h:mm:ss(.fff)`. Start + length wins over an end tag; an end tag equal to start + length − 1 is recognised as
inclusive. A loop must satisfy `0 <= start < end <= total` and be at least 0.1 s long.

Parsed results are cached by path + size + mtime (`FileCache`, 50,000 files; the full Vorbis comments are dropped
once used, so entries stay small). Concurrent requests for one file share a single read.

**Tags** (`Tags/`, `Discs/`). `TagService` gives a file's disc name and album tag fields. For FLAC and Ogg they come
from the loop header parse above, so one cached read covers the loop icon, disc names and album tags. Other formats
(MP3, M4A, ...) are read with ATL, the tag library the server already loads (the plugin compiles against it and
doesn't ship its own copy).

`AlbumTagFields` lists the file tags that become Jellyfin tags on albums. Each entry has its keys, a provider id and
a setting: `PLATFORM`/`CONSOLE`/`SYSTEM` (`VgmLoopPlatform`), `RELEASECATEGORY` (`VgmLoopCategory`) and
`SERIES`/`FRANCHISE` (`VgmLoopSeries`). `AlbumTagsPostScanTask` is an `ILibraryPostScanTask`, so it runs after every
library scan. For each music album it reads up to three tracks and takes each field from the first track that has it
(values split on U+001F, `;` and `|`, at most 5). `AlbumTagMapper.Plan` then works out the new tags. Each field
records the tags it added in its own provider id. A field only removes tags it recorded, and only when no other
field still wants them. A value the user already had as a tag isn't recorded, so it is never removed. A disabled
field's tags and record are left as they are. Albums with the Tags field locked are skipped.

**API** (`Api/`).

- `GET /VgmLoop/Items/{itemId}/LoopInfo` (authorized, per-user item visibility): codec, rate, channels, total
  samples, pre-skip, `audioDataOffset`, loop points, how the end was derived, raw tags, and a reason when there is no
  loop. Results are cached by path + size + mtime.
- `GET /VgmLoop/Items/{itemId}/Discs` (authorized, per-user visibility; album or track id): the album's named
  discs, `[{ discNumber, name, itemIds }]`. Names come from `DISCSUBTITLE` (Vorbis comments, MP4 freeform, ID3
  `TXXX`), ID3 `TSST`, or a few other spellings, read through `TagService`. For each disc it reads up to three
  tracks and uses the first name it finds; the first track of every disc is read in parallel up front. With "Show
  disc names" off, the list is empty.
- `POST /VgmLoop/LoopStatus` (authorized), body `{ itemIds: [...] }` (up to 500): `{ loopItemIds }`, the
  requested tracks the caller can see that have a playable loop. The tracks are looked up in one query and the files
  read 8 at a time, since on a network share each file costs a round trip or two. Uses the same cached LoopInfo parse.
  Empty when "Show a loop icon" is off.
- `GET /VgmLoop/web/player.js` (anonymous; a script tag can't send auth headers).

## Client (`Web/player.js`)

Registered as the jellyfin-web window plugin `VgmLoopPlayer` (`type: 'mediaplayer'`, `priority: -10`). It claims
every music track (`item.Type === 'Audio'`) so the player never changes mid-queue, and picks an engine per track:

- **Plain engine**: a hidden `<audio>` element with the stock htmlAudioPlayer's behaviour (start position, ReplayGain,
  volume persistence, fade on stop, AirPlay) minus hls.js. `getDeviceProfile` drops HLS audio transcoding profiles
  and undoes "always remux FLAC/MP3", because a window plugin can't load hls.js.
- **Loop engine**, when LoopInfo has a loop and the codec is FLAC/Vorbis/Opus: fetches the original file, strips
  `audioDataOffset` bytes, decodes it in an `AudioContext` running at the file's native sample rate, and plays it
  with `AudioBufferSourceNode` (`loopStart`/`loopEnd` from samples) → `GainNode` (volume × ReplayGain) →
  destination. Decoding and playing at the native rate keeps loop points sample-exact. One context is reused while
  consecutive loop tracks share a rate. Any failure falls back to the plain engine.

**Repeat integration.** Looping follows jellyfin-web's repeat button: Repeat One loops forever; Repeat off / all plays
the file once. Changing the mode mid-track re-anchors the position model without restarting (leaving Repeat One lets
the track play out; entering it starts looping unless the playhead is already past the loop end).
**Play-through.** A cut rip is a loop track with less than 2 s of audio after `loopEnd` (`isCutRip`). Outside Repeat
One it plays on a longer timeline instead of stopping dead: intro, N passes of the loop, then F seconds of fading while
still looping (`playthroughTimeline`; N and F come from the plugin settings via LoopInfo's `playthroughLoops` and
`playthroughFadeSeconds`, which are absent when it is off; `?vgmloop=playthrough-off` turns it off per browser). The
source keeps looping. A timeline position maps back to the file with the loop-wrap formula (`timelineToFile`), and the
anchor carries the timeline position `v0` next to the file position. The fade is a dB-linear
`exponentialRampToValueAtTime` on the GainNode down to -60 dB relative to the current volume. `_applyGain` schedules it
from the current timeline position, so volume changes, mute, seeks and pauses stay in step. The 250 ms timer ends the
track once the timeline passes its end, which counts as a natural end (the queue advances). Because jellyfin-web sizes
the seek bar from the media source's `RunTimeTicks`, the engine sets that to the play-through length while it runs and
puts the original back whenever the track ends, stops or is replaced. A stop reports the timeline fraction of the
file (`playthroughReportPosition`), so the track counts as played near the end of the fade, and fully when the fade
ends. Repeat One during play-through (even mid-fade) cancels it, ramps back up over 0.5 s and loops as usual.
Leaving Repeat One mid-track on a cut rip starts play-through with the current position as the timeline position, so
the pass in progress counts as the first. `tools/harness/playthrough.html` drives all of this with two generated
tones.

In Repeat One, jellyfin-web's Next would replay the current track; `installNextTrackOverride()` wraps
`playbackManager.nextTrack` so a user-initiated Next moves to the next queue item (wrapping like Repeat All) while
Repeat One stays selected. A natural end marks the following `nextTrack()` as an automatic advance, which keeps the
stock behaviour.

**Sticky repeat.** `PlayQueueManager.setPlaylist` (every new queue) and `reset` (queue ended) set the repeat mode to
`RepeatNone`. `installStickyRepeat()` records the mode chosen while a music queue is loaded (in `localStorage`,
`vgmloop.repeatMode`) and re-applies it when a new all-music queue starts. Because jellyfin-web calls `setPlaylist`
after the first track has started, the restore also tells the loop engine to re-check the mode.

**Position model.** An anchor `{ ctxTime0, pos0, looping }` is set on every start, seek and loop toggle. The file
position is `pos0 + (ctx.currentTime − ctxTime0) × rate`, wrapped into `[loopStart, loopEnd)` while looping, so the
seek bar shows the real file timeline and jumps back at the seam. Seeking restarts the source at the new offset.

**Preloading.** A few seconds after a track starts, the player looks up the queue item Next would lead to
(`nextQueueItem()`); if LoopInfo says it loops, its original bytes are fetched and, on desktop and below 150 MB
decoded, decoded with an `OfflineAudioContext` at the native rate (an `AudioBuffer` is not tied to a context). One slot
only: playing any other track discards it, and a failed preload just means a normal load. iOS preloads bytes only.
`?vgmloop=prefetch-off` disables it per browser.

**Played status.** When a loop track that has crossed the seam is stopped, the reported stop position is the loop
end (`stopReportPosition()`), so Jellyfin counts it as played.

**Track list decorations.** Album and playlist pages render their tracks inside `#listChildrenCollapsible`, and
jellyfin-web redraws that list with `innerHTML`. `installTrackListDecorations()` watches the DOM and handles new markup
there:

- Disc headers. The album list is `<h2 class="listGroupHeader">Disc N</h2><div>…</div>`. The script asks the Discs API
  once per album, keyed by the first track under a header because the header text is localized, and appends
  ` - name` in a `span.vgmloop-discName`. jellyfin-web only shows disc headers when an album has more than one disc (or
  starts above disc 1), so a single-disc album shows no name.
- Loop icon. New `.listItem[data-type="Audio"]` rows go to LoopStatus in batches of 100, in list order and up to 3
  at a time, so a long album fills in from the top. Rows that loop get an inline SVG (`span.vgmloop-loopIcon`) after
  the title. Answers are cached per track, so a redraw makes no request; a failed request isn't cached, so the next
  redraw asks again.

The pure functions (profile filter, position/wrap math, repeat transitions, loop-end rounding) are exported in Node
and unit tested with `node:test`; `tools/harness/` drives the real engines in a browser with mocked jellyfin-web
dependencies.
