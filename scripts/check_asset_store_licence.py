#!/usr/bin/env python3
"""Licence sweep for the asset-store branch. Exits 1 and lists every hit when the tree still
claims a licence of its own, which is what got a submission declined (request #3469260).

An Asset Store package is covered by the Asset Store EULA, so this tree must carry no LICENSE
file, no "license" field in package.json, and no MIT or open-source claim about itself. Two kinds
of line are allowed: attribution of code adapted from CoplayDev/unity-mcp, which is MIT and has to
say so, and a sentence about Coplay's own repository. CHANGELOG history is only checked for MIT:
an old entry saying "the open-source package" records what happened then.

Usage: python3 scripts/check_asset_store_licence.py [root]
"""
import json
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_DIRS = {".git", "Library", "Temp", "Logs", "node_modules"}
EXTENSIONS = (".cs", ".md", ".json", ".txt", ".asmdef")
MIT = re.compile(r"\bMIT\b")
OPEN_SOURCE = re.compile(r"open[- ]source|开源", re.IGNORECASE)
ALLOWED = re.compile(r"Coplay", re.IGNORECASE)


def hits():
    if os.path.exists(os.path.join(ROOT, "LICENSE")):
        yield "LICENSE: the file exists"

    with open(os.path.join(ROOT, "package.json"), encoding="utf-8") as handle:
        if "license" in json.load(handle):
            yield "package.json: has a \"license\" field"

    for folder, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if not name.endswith(EXTENSIONS) or name == os.path.basename(__file__):
                continue
            path = os.path.join(folder, name)
            relative = os.path.relpath(path, ROOT).replace(os.sep, "/")
            history = relative == "CHANGELOG.md"
            with open(path, encoding="utf-8", errors="replace") as handle:
                for number, line in enumerate(handle, 1):
                    if ALLOWED.search(line):
                        continue
                    if MIT.search(line) or (not history and OPEN_SOURCE.search(line)):
                        yield f"{relative}:{number}: {line.strip()[:160]}"


sys.stdout.reconfigure(encoding="utf-8", errors="replace")
found = list(hits())
for hit in found:
    print(hit)
print(f"{len(found)} licence claim(s) found." if found else "No licence of its own: ready for the Asset Store.")
sys.exit(1 if found else 0)
