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

**Loop metadata** (`Loop/`). A self-contained header parser reads only what it needs from the start of the file plus
the last 256 KB:

- skips a leading ID3v2 tag and records its length as `audioDataOffset`;
- FLAC: STREAMINFO (rate, channels, bits, total samples) and VORBIS_COMMENT;
- Ogg: reassembles the first logical stream's identification and comment packets (which may span pages, e.g. with
  large cover art); Opus or Vorbis parameters; total samples from the last page's granule position (Opus: minus
  pre-skip).

`LoopResolver` turns `LOOPSTART`/`LOOP_START`, `LOOPLENGTH`/`LOOP_LENGTH` and `LOOPEND`/`LOOP_END` into an exclusive
`[loopStart, loopEnd)` range in native-rate samples. Values may be samples, decimal seconds, `m:ss(.fff)` or
`h:mm:ss(.fff)`. Start + length wins over an end tag; an end tag equal to start + length − 1 is recognised as
inclusive. A loop must satisfy `0 <= start < end <= total` and be at least 0.1 s long.

**API** (`Api/`).

- `GET /VgmLoop/Items/{itemId}/LoopInfo` (authorized, per-user item visibility): codec, rate, channels, total
  samples, pre-skip, `audioDataOffset`, loop points, how the end was derived, raw tags, and a reason when there is no
  loop. Results are cached by path + size + mtime.
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

**Position model.** An anchor `{ ctxTime0, pos0, looping }` is set on every start, seek and loop toggle. The file
position is `pos0 + (ctx.currentTime − ctxTime0) × rate`, wrapped into `[loopStart, loopEnd)` while looping, so the
seek bar shows the real file timeline and jumps back at the seam. Seeking restarts the source at the new offset.

**Played status.** When a loop track that has crossed the seam is stopped, the reported stop position is the loop
end (`stopReportPosition()`), so Jellyfin counts it as played.

The pure functions (profile filter, position/wrap math, repeat transitions, loop-end rounding) are exported in Node
and unit tested with `node:test`; `tools/harness/` drives the real engines in a browser with mocked jellyfin-web
dependencies.
