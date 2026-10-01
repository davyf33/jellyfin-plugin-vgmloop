#!/usr/bin/env bash
# Counts which loop-tag names, value formats and sample rates exist in a music folder.
# Usage: ./loop-tag-survey.sh /path/to/vgm [ffprobe-path]
# Needs: bash, jq, ffprobe. In the Jellyfin Docker image ffprobe is /usr/lib/jellyfin-ffmpeg/ffprobe,
# but jq usually isn't there, so run this on the host or any machine that can see the files.
set -u
DIR=${1:?Pass the folder to scan}
FFPROBE=${2:-ffprobe}

find "$DIR" -type f \( -iname '*.flac' -o -iname '*.ogg' -o -iname '*.oga' -o -iname '*.opus' \) -print0 |
while IFS= read -r -d '' f; do
  "$FFPROBE" -v error -show_entries stream=codec_name,sample_rate:stream_tags:format_tags -of json "$f" |
  jq -r '(.streams[0].codec_name) as $c | (.streams[0].sample_rate) as $r
    | ((.format.tags // {}) + (.streams[0].tags // {})) | to_entries
    | map(select(.key | test("loop"; "i")))
    | if length == 0 then "\($c)\t\($r)\t(no loop tags)"
      else .[] | "\($c)\t\($r)\t\(.key)\t\(.value | if test("^[0-9]+$") then "integer"
        elif test("^[0-9]*\\.[0-9]+$") then "decimal" else "other: " + . end)" end'
done | sort | uniq -c | sort -rn
