# Jellyfin and browser notes

Findings that shaped the implementation (Jellyfin 12.1.0, jellyfin-web 12.1).

## Jellyfin

| Topic | Finding |
|---|---|
| Container string for Opus | `.opus` files report `Container: "ogg"`, same as Vorbis. The codec has to come from the stream (`Codec: "opus"`) or from our own header parser. |
| `RunTimeTicks` vs decoded length | FLAC and Vorbis match `totalSamples / rate` (rounded to 1 µs). Opus is longer by the pre-skip (e.g. 312 samples = 6.5 ms): Jellyfin counts pre-skip in the duration. Harmless for the seek bar, but position math must not assume the two are equal. |
| Script load order | All `<script>` tags in jellyfin-web's `index.html` are `defer` and in `<head>`. A classic `<script src>` inserted before `</head>` runs before any of them, so `window.VgmLoopPlayer` exists when `loadPlugins()` runs. Guarded by `InjectScript_Live1210Index_RunsBeforeDeferredBundles`. |
| Static stream | `GET /Audio/{id}/stream?static=true` returns the original bytes with Range support (206 for partial requests). |
| JSON nulls | Jellyfin's JSON output omits null properties, so `preSkip`/`reason` are absent rather than `null` in LoopInfo responses. |

## Web Audio

`AudioBufferSourceNode.loopEnd = loopEnd / rate` can round **up**: `3524459 / 48000 * 48000 = 3524459.0000000005`.
When the loop end is the last sample of the buffer, Chrome then treats the loop end as past the buffer and does not
wrap at the seam (an offline render replayed the last 128 frames; max error 0.085). `player.js` uses
`loopEndSeconds()`, the largest double with `s * rate <= loopEnd`, which brings the seam error down to ~1e-12 (float
noise). Covered by `tests/js/loop.test.mjs` and `tools/harness/loop.html`.

## Device results (default `direct` output)

| Check | iPhone Safari | Jellyfin iOS app | Safari macOS | Firefox macOS | Chrome macOS |
|---|---|---|---|---|---|
| Loop tracks start, seams clean (FLAC, Vorbis, Opus) | yes | yes | yes | yes | yes |
| Keeps playing with the screen locked | yes | yes | – | – | – |
| Lock screen / Now Playing shows the track, controls work | yes | yes | – | – | – |
| Ringer switch mutes playback | no (correct) | no | – | – | – |
| Auto-advance 32 kHz → 48 kHz loop track (new AudioContext without a gesture), then loops | yes | yes | yes | yes | yes |

Web Audio straight to `AudioContext.destination`, plus `navigator.audioSession.type = 'playback'`, is enough on iOS.
Routing through a `MediaStreamAudioDestinationNode` and a hidden `<audio>` element remains available as an opt-in
(`?vgmloop=stream`).
