# VGM Loop for Jellyfin

Seamless, sample-accurate looping of video game music in the Jellyfin **web client**. FLAC, Ogg Vorbis and Ogg Opus files tagged with `LOOPSTART` / `LOOPLENGTH` (or `LOOPEND`, `LOOP_START`, …) loop forever with no gap or click when **Repeat One** is on. Files are never re-encoded or modified.

Status: **v1 feature-complete** (milestones 1–5). Tested on Chrome, Firefox and Safari on macOS, Safari on iPhone and the Jellyfin iOS app; see [docs/browser-notes.md](docs/browser-notes.md). How it works: [docs/design.md](docs/design.md).

Requires Jellyfin **12.1**. Works in the web client and in apps that embed it (e.g. the Jellyfin iOS app); native players such as Finamp or Jellyfin Media Player are not affected.

## How it works

- Turn on **Repeat One** (the repeat button: off → all → one). A track with loop tags then plays its intro once and
  loops `LOOPSTART` → `LOOPEND` forever, with no gap. The seek bar wraps back to the loop start.
- Repeat off / Repeat all: the track plays once, start to finish, and the queue moves on.
- **Next** always skips to the next track, even in Repeat One (which stays on, so the next track loops too). At the end
  of the queue it wraps to the first track. When a track ends by itself in Repeat One it repeats, as before.
- The repeat mode you pick for music sticks: starting another track or album keeps it (stock jellyfin-web resets it
  to off), and it is remembered across page reloads in that browser. Video and other non-music queues are unaffected.
- Switching repeat mid-track takes effect immediately. Leaving Repeat One lets the current pass finish and the track
  play out; switching to Repeat One starts looping unless the loop is already behind you (an outro).
- Tracks without loop tags, and MP3/AAC files, play exactly as with the stock player.
- Loop tracks are downloaded whole and decoded in the browser (a 4-minute stereo track is ~90 MB of memory), so there's
  a short pause before they start. Playback speed control is disabled for loop tracks.

## Install (plugin repository)

1. Dashboard → Plugins → Repositories → **+**
   - Name: `VGM Loop`
   - URL: `https://raw.githubusercontent.com/davyf33/jellyfin-plugin-vgmloop/main/manifest.json`
2. Dashboard → Plugins → Catalog → **VGM Loop** → Install.
3. Restart Jellyfin (for Docker-based installs, restart the container or app).
4. Hard-refresh the browser (Cmd+Shift+R / Ctrl+Shift+R).

Updates appear under Dashboard → Plugins → Updates. Install, restart, hard-refresh.

### Manual install

Download `vgm-loop_<version>.zip` from [Releases](https://github.com/davyf33/jellyfin-plugin-vgmloop/releases), unzip it into `<config>/plugins/VGM Loop_<version>/`, make the folder owned by the user Jellyfin runs as, restart, and hard-refresh.

### Checking it works

In the browser console you should see:

```
[VgmLoop] player.js evaluated …
Loading plugin (via window): VgmLoopPlayer
[VgmLoop] constructed by pluginManager …
```

To turn it off for everyone: Dashboard → Plugins → VGM Loop → uncheck **Enable VGM Loop player** → Save, then hard-refresh.
Per-browser switches (work on phones too; open the URL once, the setting sticks in that browser):

| URL | Effect |
|---|---|
| `https://SERVER/web/?vgmloop=off` | stock player in this browser |
| `https://SERVER/web/?vgmloop=on` | VGM Loop player again |
| `https://SERVER/web/?vgmloop=stream` | loop tracks play through a hidden `<audio>` element (fallback if media keys / Now Playing don't control loop tracks in some browser) |
| `https://SERVER/web/?vgmloop=direct` | loop tracks play straight to Web Audio output (default) |

Differences from the stock audio player: no hls.js, so audio transcodes are delivered as progressive HTTP (aac/mp3/opus) and the
"always remux FLAC/MP3" settings are ignored for music (the original file is played directly). When audio normalization is
switched off, the previous track's gain is reset instead of lingering.

## Development

```bash
dotnet test                 # build + C# unit tests
node --test 'tests/js/*.test.mjs'   # player.js unit tests
tools/harness/serve.sh      # browser harness: http://localhost:8765/plain.html and /loop.html
scripts/package.sh          # artifacts/VGM Loop_<version>/ and artifacts/vgm-loop_<version>.zip
```

Releasing: bump nothing by hand. Push an annotated tag whose message is the changelog:

```bash
git tag -a v0.2.0.0 -m "LoopInfo API and tag parser"
git push origin v0.2.0.0
```

The Release workflow runs the tests, builds the zip, creates a GitHub release, and commits the new version into `manifest.json` on `main`.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
