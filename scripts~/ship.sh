#!/usr/bin/env bash
set -Eeuo pipefail

REPO="kitwright/unity-mcp"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# GUID of the Assets/unity-mcp folder in every unitypackage since v1.0.1. A staging project invents a
# new one each time it is built, and Unity then sees the folder of an update as a different asset.
ROOT_FOLDER_GUID="6fdce955fe70a7f4795087a65312a24f"

usage() {
  cat <<'EOF'
Usage: scripts~/ship.sh <version> [--dry-run] [--local]

Ships a release in one command, from a clean main that CI has already passed:
  1. check: main is in sync with origin, CI is green on HEAD, the version is new, and
     CHANGELOG.md has entries under [Unreleased]
  2. stamp CHANGELOG.md, bump package.json and the README snippets, commit "release: <version>"
  3. export the unitypackage from a staging Unity project, add the dependency manifest, validate it
  4. push main, force-push upm, tag v<version> there, create the GitHub Release with the unitypackage
  5. verify the tag, the upm branch and the asset digest, and print what is still manual

OpenUPM and the in-editor updater pick the GitHub tag and Release up by themselves. The Asset Store
upload stays manual (Asset Store Publishing Tools), see RELEASE_CHECKLIST.md.

  --dry-run  run the checks and stop
  --local    stop after step 3: the release commit stays local and nothing is pushed or published

Environment:
  UNITY_BIN    Unity executable. Default: the Hub editor that matches "unity" in package.json, since the
               staging project takes package.json's dependencies as they are.
  KW_STAGING   staging project folder, kept between releases for its Library cache. Default: <tmp>/kw-release
EOF
}

fail() {
  echo "ship: $*" >&2
  exit 1
}

info() {
  echo "==> $*"
}

VERSION="${1:-}"
if [[ -z "$VERSION" || "$VERSION" == "-h" || "$VERSION" == "--help" ]]; then
  usage
  exit 0
fi
shift || true
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || fail "invalid version: $VERSION"

DRY_RUN=0
LOCAL_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --local) LOCAL_ONLY=1 ;;
    *) fail "unknown option: $arg" ;;
  esac
done

cd "$ROOT"
for tool in git node gh tar gzip rg shasum; do
  command -v "$tool" >/dev/null 2>&1 || fail "missing required command: $tool"
done

find_unity() {
  if [[ -n "${UNITY_BIN:-}" ]]; then
    printf '%s\n' "$UNITY_BIN"
    return
  fi

  local want candidate best=""
  want="$(node -p 'require("./package.json").unity')"
  for candidate in \
    "/c/Program Files/Unity/Hub/Editor/$want"*/Editor/Unity.exe \
    "/Applications/Unity/Hub/Editor/$want"*/Unity.app/Contents/MacOS/Unity \
    "$HOME/Unity/Hub/Editor/$want"*/Editor/Unity; do
    [[ -x "$candidate" ]] && best="$candidate"
  done
  [[ -n "$best" ]] || fail "no Unity $want editor found; set UNITY_BIN"
  printf '%s\n' "$best"
}

preflight() {
  info "Checking $VERSION can ship"
  [[ "$(git rev-parse --abbrev-ref HEAD)" == "main" ]] || fail "not on main"
  [[ -z "$(git status --porcelain)" ]] || fail "the working tree is not clean"

  git fetch -q origin main --tags
  [[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || fail "main is not in sync with origin/main"

  CURRENT="$(node -p 'require("./package.json").version')"
  [[ "$VERSION" != "$CURRENT" && "$(printf '%s\n%s\n' "$CURRENT" "$VERSION" | sort -V | tail -1)" == "$VERSION" ]] ||
    fail "$VERSION is not newer than $CURRENT"
  git rev-parse -q --verify "refs/tags/v$VERSION" >/dev/null && fail "tag v$VERSION already exists"
  [[ -z "$(git ls-remote --tags origin "refs/tags/v$VERSION")" ]] || fail "tag v$VERSION already exists on origin"

  node - CHANGELOG.md <<'NODE' || fail "CHANGELOG.md has no entries under [Unreleased]"
const text = require("fs").readFileSync(process.argv[2], "utf8");
const section = text.match(/## \[Unreleased\]([\s\S]*?)(?=\n## \[|$)/);
process.exit(section && /^- /m.test(section[1]) ? 0 : 1);
NODE

  local sha total bad
  sha="$(git rev-parse HEAD)"
  read -r total bad < <(gh run list --repo "$REPO" --commit "$sha" --json status,conclusion \
    --jq 'length as $n | [.[] | select(.status != "completed" or .conclusion != "success")] | "\($n) \(length)"')
  (( total > 0 )) || fail "no CI run found for $sha; push it and let CI finish first"
  (( bad == 0 )) || fail "CI is not green on $sha ($bad of $total runs failed or are still running)"

  UNITY_BIN="$(find_unity)"
  export UNITY_BIN
}

# Takes the files from git, not from the working tree, so ignored and untracked files (a stale
# .unitypackage, UserSettings/) never get into the package.
prepare_staging() {
  local editor
  editor="$(printf '%s' "$UNITY_BIN" | grep -oE '[0-9]{4}\.[0-9]+\.[0-9]+[a-z][0-9]+' | head -1 || true)"
  [[ -n "$editor" ]] || fail "cannot read the Unity version from $UNITY_BIN"

  STAGING="${KW_STAGING:-$(cd "${TMPDIR:-/tmp}" && pwd)/kw-release}"
  if command -v cygpath >/dev/null 2>&1; then
    STAGING="$(cygpath -m "$STAGING")"
  fi

  info "Staging project $STAGING (Unity $editor)"
  mkdir -p "$STAGING/Assets" "$STAGING/Packages" "$STAGING/ProjectSettings"
  rm -rf "$STAGING/Assets/unity-mcp"
  mkdir -p "$STAGING/Assets/unity-mcp"
  git -C "$ROOT" archive HEAD | tar -x -C "$STAGING/Assets/unity-mcp"

  printf 'fileFormatVersion: 2\nguid: %s\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n' \
    "$ROOT_FOLDER_GUID" > "$STAGING/Assets/unity-mcp.meta"
  printf 'm_EditorVersion: %s\n' "$editor" > "$STAGING/ProjectSettings/ProjectVersion.txt"
  node - "$ROOT/package.json" "$STAGING/Packages/manifest.json" <<'NODE'
const fs = require("fs");
const [packageJson, manifest] = process.argv.slice(2);
const dependencies = JSON.parse(fs.readFileSync(packageJson, "utf8")).dependencies;
fs.writeFileSync(manifest, JSON.stringify({ dependencies }, null, 2));
NODE
}

stamp_and_commit() {
  info "Stamping CHANGELOG.md and bumping $CURRENT -> $VERSION"
  node - CHANGELOG.md "$VERSION" "$(date +%F)" <<'NODE'
const fs = require("fs");
const [file, version, date] = process.argv.slice(2);
const text = fs.readFileSync(file, "utf8");
const newline = text.includes("\r\n") ? "\r\n" : "\n";
const heading = "## [Unreleased]";
const at = text.indexOf(heading);
if (at < 0) throw new Error("no [Unreleased] heading in " + file);
const end = at + heading.length;
fs.writeFileSync(file, text.slice(0, end) + newline + newline + "## [" + version + "] - " + date + text.slice(end));
NODE
  bash "$ROOT/scripts~/release.sh" "$VERSION" --bump-only --no-wrapper
  git add CHANGELOG.md package.json README.md README_CN.md
  git commit -q -m "release: $VERSION"
  COMMITTED=1
}

verify() {
  info "Verifying v$VERSION"
  git fetch -q origin --tags
  [[ "$(git rev-parse "v$VERSION^{commit}")" == "$(git rev-parse origin/upm)" ]] || fail "tag v$VERSION is not on origin/upm"

  local want have
  want="$(shasum -a 256 "$PACKAGE" | cut -d' ' -f1)"
  have="$(gh release view "v$VERSION" --repo "$REPO" --json assets --jq '.assets[0].digest' | sed 's/^sha256://')"
  [[ "$want" == "$have" ]] || fail "the asset on GitHub ($have) is not the package built here ($want)"
  echo "tag v$VERSION is on origin/upm, release asset sha256 $want"
}

COMMITTED=0
PUSHED=0
on_exit() {
  local code=$?
  if (( code != 0 && COMMITTED == 1 && PUSHED == 0 )); then
    echo "ship: the release commit is local only; undo it with: git reset --hard origin/main" >&2
  fi
}
trap on_exit EXIT

preflight
if (( DRY_RUN == 1 )); then
  info "Dry run: every check passed, $CURRENT -> $VERSION would ship with $UNITY_BIN"
  exit 0
fi

stamp_and_commit
prepare_staging
export UNITY_PROJECT_ROOT="$STAGING"
export UNITY_EXPORT_TIMEOUT_SECONDS="${UNITY_EXPORT_TIMEOUT_SECONDS:-1200}"

# CI already ran the tests on this code; only the version strings changed since.
bash "$ROOT/scripts~/release.sh" "$VERSION" --no-tests --no-wrapper
[[ -z "$(git status --porcelain)" ]] || fail "release.sh changed tracked files; see git status"
PACKAGE="$STAGING/release-artifacts/$VERSION/KitWright.UnityMcp.v$VERSION.unitypackage"

if (( LOCAL_ONLY == 1 )); then
  info "Built $PACKAGE; nothing pushed. To publish it:"
  echo "  git push origin main"
  echo "  UNITY_BIN='$UNITY_BIN' UNITY_PROJECT_ROOT='$STAGING' bash scripts~/release.sh $VERSION --publish-github --no-export --no-tests --no-wrapper"
  exit 0
fi

git push origin main
PUSHED=1
bash "$ROOT/scripts~/release.sh" "$VERSION" --publish-github --no-export --no-tests --no-wrapper
verify

info "Done: v$VERSION is on GitHub"
echo "  upm / Git URL : #upm and #v$VERSION already serve it"
echo "  OpenUPM       : indexes the tag on its own, check with: npm view com.kitwright.unity.mcp --registry https://package.openupm.com versions"
echo "  CI            : gh run list --repo $REPO --commit $(git rev-parse HEAD)"
echo "  Asset Store   : manual, upload through Asset Store Publishing Tools (see RELEASE_CHECKLIST.md)"
