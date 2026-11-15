# Jellyfin and browser notes

Findings that shaped the implementation (Jellyfin 12.1.0, jellyfin-web 12.1; rechecked against 12.2.0, where the audio player, play queue and index.html are unchanged; run end to end against 12.4.0).

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

## Device results (1.0, Web Audio output only)

| Check | iPhone Safari | Jellyfin iOS app | Safari macOS | Firefox macOS | Chrome macOS |
|---|---|---|---|---|---|
| Loop tracks start, seams clean (FLAC, Vorbis, Opus) | yes | yes | yes | yes | yes |
| Keeps playing with the screen locked | yes | yes | – | – | – |
| Lock screen / Now Playing shows the track, controls work | yes | yes | – | – | – |
| Ringer switch mutes playback | no (correct) | no | – | – | – |
| Auto-advance 32 kHz → 48 kHz loop track (new AudioContext without a gesture), then loops | yes | yes | yes | yes | yes |

Web Audio straight to `AudioContext.destination`, plus `navigator.audioSession.type = 'playback'`, keeps loop tracks
playing in the background on iOS. The lock-screen row above no longer holds: later iOS versions show the title of a
Web Audio page but no working transport buttons or position, in Safari and the app. Routing Web Audio into an
`<audio>` element through a `MediaStreamAudioDestinationNode` (the old `?vgmloop=stream`) didn't help either (iOS 27
simulator: title only, no position). jellyfin-web only sets `mediaSession.metadata` and action handlers, never
`playbackState` or `setPositionState`, because a media element supplies those itself.

So while a loop track plays, the loop engine also plays a silent file-backed `<audio>` (`audio.vgmLoopKeepAlive`, a
blob WAV as long as the track's seek bar: the file, or the play-through length) and keeps it at the track's position.
iOS then shows Now Playing from that element: play/pause, the seek bar, and jellyfin-web's mediaSession handlers.

1.3.0.0 used a 1 s looping clip plus `mediaSession.setPositionState`. On an iPhone, play/pause worked but the seek
bar jumped between the clip's position and the reported one, and the lock screen's previous/next buttons were greyed
out (AirPods double/triple tap still skipped). The simulator's lock screen never shows transport buttons, even for a
plain `<audio>` file, so this needs a real device.

1.3.1.0 on an iPhone: the seek bar was steady, but it could not be dragged and previous/next stayed greyed. Plain tracks
showed skip-10 buttons instead of previous/next. WebKit's source (NowPlayingManager, RemoteCommandListenerCocoa)
explains both:
- `setActionHandler` is dropped while the page has no media session yet, and jellyfin-web sets its handlers at
  startup. With none registered, WebKit offers its default skip and seek buttons, not previous/next. The player wraps
  `setActionHandler` and sets the handlers again on every `playing` event.
- Seek and skip buttons are only enabled when the media that started last can seek. An AudioContext can't, so on
  resume the loop engine starts the context before the silent element.

The simulator's Now Playing view never draws the buttons, but MediaRemote can still be queried there. A small
MediaRemote client run with `xcrun simctl spawn` (entitlements embedded in a `__TEXT,__entitlements` section) showed
the enabled commands. Before the fix, loop tracks got play/pause only; after it, they got play, pause, stop,
previous, next and seek-to-position. Commands sent the same way (seek, pause, play, next, previous) all worked.
