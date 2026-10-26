#!/usr/bin/env bash
# Runs the end-to-end suite against a fresh Jellyfin with this plugin. Everything lives under $WORK (default
# /tmp/vgmloop-e2e) and is recreated on each run: the generated music library, the server's data/config/cache.
#   JELLYFIN=/path/to/jellyfin tools/e2e/run.sh
# JELLYFIN is the server executable (a portable build, or `dotnet publish Jellyfin.Server` from the jellyfin repo).
# Needs python3, ffmpeg and curl. jellyfin-web is not needed: the web dir is the index.html/config.json test copies.
# LIVE=1 also runs tools/e2e/live.mjs (player.js in Chromium against the same server; needs node and Playwright,
# PLAYWRIGHT=/path/to/playwright/index.mjs and CHROMIUM=/path/to/chromium if they aren't found by default).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
: "${JELLYFIN:?Set JELLYFIN to the Jellyfin server executable}"
WORK="${WORK:-${TMPDIR:-/tmp}/vgmloop-e2e}"
PORT=8096
VERSION="${VERSION:-$(sed -n 's/^version: *"\(.*\)"/\1/p' "$ROOT/build.yaml")}"

rm -rf "$WORK" && mkdir -p "$WORK"/{data/plugins,config,cache,log,web}
python3 -I "$ROOT/tools/e2e/gen_library.py" "$WORK/music"
(cd "$ROOT" && scripts/package.sh "$VERSION" >/dev/null)
cp -r "$ROOT/artifacts/VGM Loop_$VERSION" "$WORK/data/plugins/"
cp "$ROOT/tests/Jellyfin.Plugin.VgmLoop.Tests/TestData/index-12.1.0.html" "$WORK/web/index.html"
cp "$ROOT/tests/Jellyfin.Plugin.VgmLoop.Tests/TestData/config-12.1.0.json" "$WORK/web/config.json"

if curl -s -o /dev/null "http://localhost:$PORT/"; then
  echo "Something is already listening on port $PORT; stop it first." >&2
  exit 1
fi
"$JELLYFIN" --datadir "$WORK/data" --configdir "$WORK/config" --cachedir "$WORK/cache" --logdir "$WORK/log" \
  --webdir "$WORK/web" --ffmpeg "$(command -v ffmpeg)" >"$WORK/server.out" 2>&1 &
SERVER=$!
stop_server() { kill "$SERVER" 2>/dev/null || true; wait "$SERVER" 2>/dev/null || true; }
trap stop_server EXIT
E2E_IDS="$WORK/ids.json" python3 -I "$ROOT/tools/e2e/e2e.py" "http://localhost:$PORT" "$WORK/music" "$VERSION"
if [ -n "${LIVE:-}" ]; then
  node "$ROOT/tools/e2e/live.mjs" "http://localhost:$PORT"
fi
