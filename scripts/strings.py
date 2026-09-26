#!/usr/bin/env python3
"""Keeps Mindtalk/Resources/Localizable.xcstrings in step with the code.

1. Pulls in every string the last Debug build extracted (xcstringstool sync).
2. Gives each string an explicit Swedish value (= the key). Swedish is the source
   language, but English is the fallback for all other Macs — without a real sv
   table, a Swedish Mac would fall back to English.
3. Lists strings that still need an English translation.

Run after `make build`:  python3 scripts/strings.py
"""
import glob, json, subprocess, sys

CATALOG = "Mindtalk/Resources/Localizable.xcstrings"
data = glob.glob("build/Build/Intermediates.noindex/Mindtalk.build/Debug/Mindtalk.build/Objects-normal/*/*.stringsdata")
if data:
    subprocess.run(["xcrun", "xcstringstool", "sync", CATALOG, "--stringsdata", *data], check=True)

cat = json.load(open(CATALOG))
untranslated = []
for key, entry in cat["strings"].items():
    locs = entry.setdefault("localizations", {})
    locs["sv"] = {"stringUnit": {"state": "translated", "value": key}}
    if "en" not in locs and any(c.isalpha() for c in key):
        untranslated.append(key)
json.dump(cat, open(CATALOG, "w"), ensure_ascii=False, indent=2, sort_keys=True)

if untranslated:
    print("Saknar engelsk översättning:")
    for k in sorted(untranslated):
        print("  " + k)
    sys.exit(1)
print(f"OK – {len(cat['strings'])} strängar, svenska och engelska.")
