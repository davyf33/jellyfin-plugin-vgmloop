# VGM Loop for Jellyfin

Seamless, sample-accurate looping of video game music in the Jellyfin **web client**. FLAC, Ogg Vorbis and Ogg Opus files tagged with `LOOPSTART` / `LOOPLENGTH` (or `LOOPEND`, `LOOP_START`, …) loop forever with no gap or click when **Repeat One** is on. Files are never re-encoded or modified.

Status: **1.0**. Tested on Chrome, Firefox and Safari on macOS, Safari on iPhone and the Jellyfin iOS app; see [docs/browser-notes.md](docs/browser-notes.md). How it works: [docs/design.md](docs/design.md).

Requires Jellyfin **12.2** (for 12.1, install VGM Loop 1.0). Works in the web client and in apps that embed it (e.g. the Jellyfin iOS app); native players such as Finamp or Jellyfin Media Player are not affected.

## How it works

- Turn on **Repeat One** (the repeat button: off → all → one). A track with loop tags then plays its intro once and
  loops `LOOPSTART` → `LOOPEND` forever, with no gap. The seek bar wraps back to the loop start.
- Repeat off / Repeat all: the track plays once, start to finish, and the queue moves on. The exception is a cut rip, a
  loop track that stops right at the loop end (less than 2 s of audio after it). Played once, it would cut off
  mid-phrase, so instead it plays the intro, the loop twice, then fades out over 10 s while still looping, the way game
  OSTs end their tracks. Then the queue moves on. The seek bar shows that whole length, and the track counts as played
  once the fade ends. Files with a real ending or their own fade after the loop play as before. Set the loop count
  (1-5) and fade length (0-30 s), or turn this off, in the plugin settings.
- **Next** always skips to the next track, even in Repeat One (which stays on, so the next track loops too). At the end
  of the queue it wraps to the first track. When a track ends by itself in Repeat One it repeats, as before.
- The repeat mode you pick for music sticks: starting another track or album keeps it (stock jellyfin-web resets it
  to off), and it is remembered across page reloads in that browser. Video and other non-music queues are unaffected.
- Switching repeat mid-track takes effect immediately. Leaving Repeat One lets the current pass finish and the track
  play out; switching to Repeat One starts looping unless the loop is already behind you (an outro). On a cut rip,
  leaving Repeat One starts the play-through from where you are (the pass you're in counts as the first), and turning
  Repeat One on during the fade cancels it, brings the volume back up and keeps looping.
- Tracks without loop tags, and MP3/AAC files, play exactly as with the stock player.
- Loop tracks are downloaded whole and decoded in the browser (a 4-minute stereo track is ~90 MB of memory). To avoid a
  pause between tracks, the next track in the queue is preloaded a few seconds into the current one when it has loop
  tags: downloaded and decoded on desktop, downloaded only on iPhone/iPad (to keep memory low). Skipping elsewhere
  discards the preload. Playback speed control is disabled for loop tracks.

## Loop icon and disc names

Tracks with loop tags get a small loop icon after the title in album and playlist track lists, so you can see which
ones loop before you play them. To turn it off, uncheck **Show a loop icon on looping tracks** in the plugin settings.

Albums with more than one disc show the disc name from the tags next to each header, like **Disc 2 - Arrange Album**.
The plugin reads `DISCSUBTITLE` (FLAC, Ogg, M4A, and MP3 `TXXX`; this is what MusicBrainz Picard writes) or `TSST`
(the ID3v2.4 "set subtitle" frame in MP3). `SETSUBTITLE`, `DISCNAME` and `DISCTITLE` also work. The plugin only reads
the tags and doesn't modify your files. To turn it off, go to Dashboard → Plugins → VGM Loop and uncheck **Show disc
names on album pages**.

## Album tags: platform, release category, series

The plugin turns three file tags into Jellyfin tags on each album after every library scan:

| File tag | Also read | Example values |
|---|---|---|
| `PLATFORM` | `CONSOLE`, `SYSTEM` | Nintendo 3DS, Wii U |
| `RELEASECATEGORY` | | Official Soundtrack, Official Arrangement, Official Compilation, Game Rip, Third-Party Game Rip |
| `SERIES` | `FRANCHISE` | Animal Crossing, Super Mario, Splatoon |

In the music library, open Filter → Tags and pick one to see only those albums. The tags also show on the album page,
and clicking one lists everything with it. For more than one value (a game on two consoles, say), separate them with
`;` or `|`, or use one tag per value. Each field takes up to 5 values.

The plugin reads the first few tracks of each album, so only one track needs the tags, though tagging every track is
fine. It only changes the album in Jellyfin's database and never writes to your files. When you change or remove a tag
in the files, the next scan updates the album: changing `RELEASECATEGORY` from Game Rip to Official Soundtrack swaps
that tag, and clearing `SERIES` removes only the series tag. Each field keeps track of the tags it added, so it never
removes another field's tags or a tag you added yourself in Jellyfin. An album whose Tags field you locked in the
metadata editor is left alone.

After installing, run a library scan (Dashboard → Libraries → Scan All Libraries) to tag what's already there. Each
field has its own switch in the plugin settings. Turning one off stops updating those tags but leaves the ones already
there.

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
| `https://SERVER/web/?vgmloop=prefetch-off` | don't preload the next loop track (e.g. to save mobile data); `prefetch-on` restores it |
| `https://SERVER/web/?vgmloop=playthrough-off` | play cut rips once, stopping at the loop end, instead of looping and fading; `playthrough-on` restores it |

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
