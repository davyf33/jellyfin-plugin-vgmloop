#!/usr/bin/env python3
"""Adds (or replaces) a version entry in manifest.json (Jellyfin plugin repository format)."""
import argparse
import datetime
import hashlib
import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parent.parent


def build_yaml():
    text = (ROOT / "build.yaml").read_text()
    def field(name):
        m = re.search(rf'^{name}:\s*"([^"]*)"', text, re.M)
        return m.group(1) if m else ""
    return {k: field(k) for k in ("name", "guid", "targetAbi", "owner", "overview", "category")}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True)
    ap.add_argument("--zip", required=True)
    ap.add_argument("--url", required=True)
    ap.add_argument("--changelog-file")
    args = ap.parse_args()

    meta = build_yaml()
    path = ROOT / "manifest.json"
    manifest = json.loads(path.read_text()) if path.exists() else []
    plugin = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
    if plugin is None:
        plugin = {"guid": meta["guid"], "versions": []}
        manifest.append(plugin)
    plugin.update({
        "name": meta["name"],
        "description": "Seamless, sample-accurate looping of LOOPSTART/LOOPLENGTH-tagged FLAC, Ogg Vorbis and Opus "
                       "in the Jellyfin web client, tied to the Repeat One button. No re-encoding.",
        "overview": meta["overview"],
        "owner": meta["owner"],
        "category": meta["category"],
    })

    changelog = pathlib.Path(args.changelog_file).read_text().strip() if args.changelog_file else ""
    entry = {
        "version": args.version,
        "changelog": changelog,
        "targetAbi": meta["targetAbi"],
        "sourceUrl": args.url,
        "checksum": hashlib.md5(pathlib.Path(args.zip).read_bytes()).hexdigest(),
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    versions = [v for v in plugin["versions"] if v.get("version") != args.version]
    versions.insert(0, entry)
    versions.sort(key=lambda v: [int(x) for x in v["version"].split(".")], reverse=True)
    plugin["versions"] = versions

    path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"manifest.json: {meta['name']} {args.version} md5 {entry['checksum']}")


if __name__ == "__main__":
    main()
