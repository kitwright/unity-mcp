#!/usr/bin/env python3
"""Build a .unitypackage from this UPM repo without opening Unity.

WORK IN PROGRESS - the file this produces still makes Unity throw a
NullReferenceException in PackageImportTreeView on import, so do not ship its
output. Asset Store releases are exported through Asset Store Publishing Tools
instead. To finish this, diff the output against a real Unity export.

Format: gzipped tar, one directory per asset named by its GUID, containing
  asset       - raw file bytes (omitted for folders)
  asset.meta  - the .meta file
  pathname    - destination path inside the importing project
"""
import hashlib
import io
import os
import re
import subprocess
import sys
import tarfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREFIX = "Assets/KitWright/MCPForUnity"
OUT = os.path.join(REPO, "KitWright-MCPForUnity.unitypackage")

EXCLUDE_PREFIXES = (".github/", "scripts~/", "Documentation~/", "Tests/")
EXCLUDE_FILES = {"CONTRIBUTING.md", "RELEASE_CHECKLIST.md", ".editorconfig", ".gitattributes", ".gitignore"}

GUID_RE = re.compile(rb"^guid:\s*([0-9a-fA-F]{32})\s*$", re.M)

FOLDER_META = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:\x20
  assetBundleName:\x20
  assetBundleVariant:\x20
"""


def synth_folder_meta(dest_path):
    """Meta for a prefix folder that has no counterpart in the repo.

    GUID is derived from the destination path so rebuilds keep it stable and
    Unity treats an update as the same folder rather than a new asset.
    """
    guid = hashlib.md5(dest_path.encode()).hexdigest()
    return guid, FOLDER_META.format(guid=guid).encode()


def tracked_files():
    out = subprocess.check_output(["git", "ls-files"], cwd=REPO, text=True)
    return [p for p in out.splitlines() if p]


def guid_of(meta_bytes, path):
    m = GUID_RE.search(meta_bytes)
    if not m:
        sys.exit(f"no guid in meta for {path}")
    return m.group(1).decode()


def add(tar, guid, pathname, meta_bytes, asset_bytes):
    def entry(name, data):
        info = tarfile.TarInfo(f"{guid}/{name}")
        info.size = len(data)
        info.mode = 0o644
        tar.addfile(info, io.BytesIO(data))

    entry("pathname", pathname.encode())
    entry("asset.meta", meta_bytes)
    if asset_bytes is not None:
        entry("asset", asset_bytes)


def main():
    files = tracked_files()
    metas = {p for p in files if p.endswith(".meta")}
    assets = [p for p in files if not p.endswith(".meta")]

    included = set()
    count = 0
    with tarfile.open(OUT, "w:gz") as tar:
        # PREFIX folders exist only in the destination project, so nothing in the
        # repo carries their .meta. Without these entries Unity's import tree
        # cannot resolve a parent for the first real folder and throws an NRE.
        prefix_parts = PREFIX.split("/")[1:]
        for i in range(len(prefix_parts)):
            dest = "Assets/" + "/".join(prefix_parts[: i + 1])
            guid, mb = synth_folder_meta(dest)
            add(tar, guid, dest, mb, None)
            count += 1

        for rel in sorted(assets):
            if rel.startswith(EXCLUDE_PREFIXES) or rel in EXCLUDE_FILES:
                continue
            meta_rel = rel + ".meta"
            if meta_rel not in metas:
                print(f"skip (no .meta): {rel}")
                continue

            # every ancestor folder needs its own entry so Unity rebuilds the tree
            parts = rel.split("/")[:-1]
            for i in range(len(parts)):
                folder = "/".join(parts[: i + 1])
                if folder in included:
                    continue
                dest = f"{PREFIX}/{folder}"
                fmeta = folder + ".meta"
                if fmeta in metas:
                    with open(os.path.join(REPO, fmeta), "rb") as f:
                        mb = f.read()
                    fguid = guid_of(mb, fmeta)
                else:
                    fguid, mb = synth_folder_meta(dest)
                add(tar, fguid, dest, mb, None)
                included.add(folder)
                count += 1

            with open(os.path.join(REPO, meta_rel), "rb") as f:
                mb = f.read()
            with open(os.path.join(REPO, rel), "rb") as f:
                ab = f.read()
            add(tar, guid_of(mb, meta_rel), f"{PREFIX}/{rel}", mb, ab)
            count += 1

    print(f"{count} entries -> {OUT} ({os.path.getsize(OUT) / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
