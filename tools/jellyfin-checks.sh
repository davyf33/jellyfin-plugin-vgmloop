#!/usr/bin/env bash
# Gathers the server-side facts the loop player depends on (version, headers, static streams).
# Usage:
#   JF=http://your-server:8096 KEY=your_api_key ./jellyfin-checks.sh ITEM_ID [ITEM_ID ...]
# Optional: USER_ID=... (defaults to the first user returned by /Users)
# Needs: curl, jq, sha256sum. Item IDs are in the URL of a track's page (details?id=...).
set -u
: "${JF:?Set JF to your server URL, e.g. http://192.168.1.10:8096}"
: "${KEY:?Set KEY to an API key from Dashboard > API Keys}"
H="Authorization: MediaBrowser Token=\"$KEY\""

echo "== Server"
curl -s "$JF/System/Info/Public" | jq '{Version, ProductName, OperatingSystem}'

echo "== Headers on /web/ (a CSP here would affect script injection)"
curl -sI "$JF/web/" | grep -iE '^(content-security-policy|x-frame-options|server|cache-control):' || echo "(no CSP or frame headers)"

echo "== jellyfin-web plugin list in config.json"
curl -s "$JF/web/config.json" | jq -c '.plugins'

USER_ID=${USER_ID:-$(curl -s -H "$H" "$JF/Users" | jq -r '.[0].Id')}

for ID in "$@"; do
  echo
  echo "== Item $ID"
  curl -s -H "$H" "$JF/Items/$ID?userId=$USER_ID&fields=MediaSources,Path" | jq '{
    Name, Type, MediaType, Path, RunTimeTicks, NormalizationGain,
    MediaSources: [.MediaSources[]? | {Container, Size, RunTimeTicks, SupportsDirectPlay,
      SupportsDirectStream, SupportsTranscoding, ETag,
      Streams: [.MediaStreams[]? | {Type, Codec, SampleRate, Channels, BitDepth}]}]}'

  echo "-- Static stream: status, type, length, sha256"
  curl -s -H "$H" -o /tmp/jf_static.bin -w 'HTTP %{http_code}  %{content_type}  %{size_download} bytes\n' \
    "$JF/Audio/$ID/stream?static=true"
  sha256sum /tmp/jf_static.bin | cut -d' ' -f1

  echo "-- Range request (expect 206)"
  curl -s -H "$H" -r 0-99 -o /dev/null -w 'HTTP %{http_code}  %{size_download} bytes\n' \
    "$JF/Audio/$ID/stream?static=true"
done

echo
echo "Now run 'sha256sum' on each original file on disk and check it matches the hash above."
