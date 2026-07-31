#!/usr/bin/env python3
"""Insert or replace a version entry in a Jellyfin plugin manifest."""
import json, os, sys, pathlib

path      = pathlib.Path(os.environ["MANIFEST_PATH"])
version   = os.environ["VERSION"]
checksum  = os.environ["CHECKSUM"]
abi       = os.environ["TARGET_ABI"]
changelog = os.environ["CHANGELOG"]
src       = os.environ["SOURCE_URL"]
stamp     = os.environ["TIMESTAMP"]
guid      = os.environ["PLUGIN_GUID"]
name      = os.environ["PLUGIN_NAME"]
owner     = os.environ["PLUGIN_OWNER"]

manifest = json.loads(path.read_text()) if path.exists() and path.read_text().strip() else []

plugin = next((p for p in manifest if p.get("guid", "").lower() == guid.lower()), None)
if plugin is None:
    plugin = {
        "category": "General", "guid": guid, "name": name,
        "description": "Synchronize anime watch status between Jellyfin and anime tracking sites.",
        "owner": owner, "overview": "Synchronize anime watch status", "versions": [],
    }
    manifest.append(plugin)

entry = {"checksum": checksum, "changelog": changelog, "targetAbi": abi,
         "sourceUrl": src, "timestamp": stamp, "version": version}

plugin["versions"] = [v for v in plugin["versions"] if v.get("version") != version]
plugin["versions"].insert(0, entry)

def key(v):
    return tuple(int(x) for x in v["version"].split("."))
plugin["versions"].sort(key=key, reverse=True)

path.write_text(json.dumps(manifest, indent=2) + "\n")
print(f"manifest now advertises {len(plugin['versions'])} version(s); newest = {plugin['versions'][0]['version']}")
