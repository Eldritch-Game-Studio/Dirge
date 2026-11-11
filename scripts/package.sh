#!/usr/bin/env bash
# Assembles the installable addon ZIP: dist/Dirge-v<version>.zip (version from addons/dirge/plugin.cfg).
#
#   scripts/package.sh                 dist/Dirge-v<version>.zip
#   scripts/package.sh --suffix -partial    dist/Dirge-v<version>-partial.zip (CI diagnostic without every native binary)
#
# Contents: addons/dirge/** (including every registered native library; model/bin, model/obj and every
# obj/ directory excluded; .uid files kept so the host project's resource ids stay stable).
# Repository LICENSE, README.md, CHANGELOG.md and THIRD_PARTY_NOTICES.md live under addons/dirge/.
# The NecronomiDOM dependency is never bundled; scripts/fetch-deps.sh installs it.
set -euo pipefail
cd "$(dirname "$0")/.."

suffix=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --suffix) suffix="$2"; shift 2 ;;
    *) echo "package: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

version="$(sed -n 's/^version="\([^"]*\)".*/\1/p' addons/dirge/plugin.cfg)"
[[ -n "$version" ]] || { echo "package: no version= in addons/dirge/plugin.cfg" >&2; exit 1; }
command -v zip >/dev/null || { echo "package: zip is required" >&2; exit 2; }

for required in LICENSE README.md CHANGELOG.md THIRD_PARTY_NOTICES.md addons/dirge/plugin.cfg; do
  [[ -f "$required" ]] || { echo "package: missing $required" >&2; exit 1; }
done

name="Dirge-v${version}${suffix}"
out="dist/${name}.zip"
mkdir -p dist
staging="$(mktemp -d "$PWD/dist/.package.XXXXXX")"
trap 'rm -rf "$staging"' EXIT

# Build the file list explicitly so the exclusions are auditable (zip -x globbing is anchor-sensitive).
files=()
while IFS= read -r file; do
  files+=("$file")
done < <(
  find addons/dirge -type f \
    -not -path 'addons/dirge/model/bin/*' \
    -not -path 'addons/dirge/README.md' \
    -not -path 'addons/dirge/LICENSE' \
    -not -path 'addons/dirge/CHANGELOG.md' \
    -not -path 'addons/dirge/THIRD_PARTY_NOTICES.md' \
    -not -path '*/obj/*' \
    -not -name '.DS_Store' \
    -not -name '*.import.tmp' | LC_ALL=C sort
)
[[ ${#files[@]} -gt 0 ]] || { echo "package: addons/dirge looks empty" >&2; exit 1; }

zip -X -q "$staging/$name.zip" "${files[@]}"
mkdir -p "$staging/addons/dirge"
cp LICENSE README.md CHANGELOG.md THIRD_PARTY_NOTICES.md "$staging/addons/dirge/"
(cd "$staging" && zip -X -q "$name.zip" addons/dirge/LICENSE addons/dirge/README.md \
  addons/dirge/CHANGELOG.md addons/dirge/THIRD_PARTY_NOTICES.md)

# Validate the final ZIP against its own manifest before replacing a published package.
# Only an explicitly labelled diagnostic may omit platforms.
if [[ "$suffix" = "-partial" ]]; then
  python3 scripts/package-native.py "$staging/$name.zip" --partial
else
  python3 scripts/package-native.py "$staging/$name.zip"
fi
mv "$staging/$name.zip" "$out"

count=$(( ${#files[@]} + 4 ))
size=$(wc -c < "$out")
printf 'package: %s — %d files, %d bytes (%d KiB)\n' "$out" "$count" "$size" "$(( (size + 1023) / 1024 ))"
if [[ -d addons/dirge/bin ]]; then
  echo "package: native binaries:"
  find addons/dirge/bin -type f | LC_ALL=C sort | sed 's/^/  /'
else
  echo "package: no native binaries (addons/dirge/bin absent)"
fi
