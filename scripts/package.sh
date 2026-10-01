#!/usr/bin/env bash
# Builds the plugin and lays it out as a Jellyfin plugin folder + zip under artifacts/.
# Usage: scripts/package.sh [version]   (default: version from build.yaml)
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=${1:-$(sed -n 's/^version: *"\(.*\)"/\1/p' build.yaml)}
GUID=$(sed -n 's/^guid: *"\(.*\)"/\1/p' build.yaml)
ABI=$(sed -n 's/^targetAbi: *"\(.*\)"/\1/p' build.yaml)
NAME="VGM Loop"
OUT="artifacts/${NAME}_${VERSION}"

rm -rf artifacts && mkdir -p "$OUT"
dotnet publish Jellyfin.Plugin.VgmLoop/Jellyfin.Plugin.VgmLoop.csproj -c Release \
  -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION" \
  -o artifacts/publish --nologo -v q
cp artifacts/publish/Jellyfin.Plugin.VgmLoop.dll "$OUT/"

cat > "$OUT/meta.json" <<JSON
{
  "category": "General",
  "changelog": "",
  "description": "Seamless tagged-loop playback for video game music in the web client.",
  "guid": "$GUID",
  "name": "$NAME",
  "overview": "Seamless tagged-loop playback for video game music",
  "owner": "davyf33",
  "targetAbi": "$ABI",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
JSON

(cd artifacts && zip -qj "vgm-loop_${VERSION}.zip" "${NAME}_${VERSION}/Jellyfin.Plugin.VgmLoop.dll")
echo "folder: $OUT"
echo "zip:    artifacts/vgm-loop_${VERSION}.zip  md5 $(md5 -q "artifacts/vgm-loop_${VERSION}.zip" 2>/dev/null || md5sum "artifacts/vgm-loop_${VERSION}.zip" | cut -d' ' -f1)"
