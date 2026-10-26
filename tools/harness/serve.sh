#!/usr/bin/env bash
# Browser harness for player.js with mocked jellyfin-web deps.
#   tools/harness/serve.sh [port]   then open http://localhost:8765/plain.html, /loop.html and /playthrough.html
# loop.html needs the wii_menu* fixtures in ./fixtures (not in the repo).
# Needs python3 and ffmpeg (for the short test tones).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PORT="${1:-8765}"
WORK="${TMPDIR:-/tmp}/vgmloop-harness"
mkdir -p "$WORK"
ln -sf "$ROOT/Jellyfin.Plugin.VgmLoop/Web/player.js" "$WORK/player.js"
for f in "$ROOT"/tools/harness/*.html "$ROOT"/tools/harness/li_*.json; do ln -sf "$f" "$WORK/"; done
if [ -d "$ROOT/fixtures" ]; then
  for f in "$ROOT"/fixtures/wii_menu*; do ln -sf "$f" "$WORK/"; done
fi
[ -f "$WORK/tone.flac" ] || ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=440:duration=3:sample_rate=32000" -ac 2 "$WORK/tone.flac"
[ -f "$WORK/tone.mp3" ] || ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=660:duration=3:sample_rate=44100" -ac 2 -b:a 128k "$WORK/tone.mp3"
# Play-through: a 3 s "cut rip" (loop 1 s..3 s ends at the end of the file) and a 6 s file with a 3 s ending after the loop.
[ -f "$WORK/cut.flac" ] || ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=330:duration=3:sample_rate=32000" -ac 2 "$WORK/cut.flac"
[ -f "$WORK/tail.flac" ] || ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=550:duration=6:sample_rate=32000" -ac 2 "$WORK/tail.flac"
echo "Serving $WORK on http://localhost:$PORT (plain.html, loop.html, playthrough.html)"
exec python3 "$ROOT/tools/harness/rangeserver.py" "$PORT" "$WORK"
