#!/usr/bin/env bash
# Assembles the installable addon ZIP: dist/Dirge-v<version>.zip (version from addons/dirge/plugin.cfg).
#
#   scripts/package.sh                 dist/Dirge-v<version>.zip
#   scripts/package.sh --suffix -partial    dist/Dirge-v<version>-partial.zip (CI diagnostic without every native binary)
#
# Contents: addons/dirge/** (including bin/<platform>/ native libraries when present; model/bin, model/obj and every
# obj/ directory excluded; .uid files kept so the host project's resource ids stay stable), LICENSE, README.md,
# CHANGELOG.md. The NecronomiDOM dependency is never bundled; scripts/fetch-deps.sh installs it.
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

for required in LICENSE README.md CHANGELOG.md addons/dirge/plugin.cfg; do
  [[ -f "$required" ]] || { echo "package: missing $required" >&2; exit 1; }
done

# A normal release must satisfy every GDExtension mapping. Only an explicitly
# labelled diagnostic may omit platforms; never publish it as a full package.
if [[ "$suffix" != "-partial" ]]; then
  python3 scripts/package-native.py
fi

name="Dirge-v${version}${suffix}"
out="dist/${name}.zip"
mkdir -p dist
rm -f "$out"

# Build the file list explicitly so the exclusions are auditable (zip -x globbing is anchor-sensitive).
files=()
while IFS= read -r file; do
  files+=("$file")
done < <(
  find addons/dirge -type f \
    -not -path 'addons/dirge/model/bin/*' \
    -not -path '*/obj/*' \
    -not -name '.DS_Store' \
    -not -name '*.import.tmp' | LC_ALL=C sort
  printf '%s\n' LICENSE README.md CHANGELOG.md
)
[[ ${#files[@]} -gt 4 ]] || { echo "package: addons/dirge looks empty" >&2; exit 1; }

zip -X -q "$out" "${files[@]}"

count=${#files[@]}
size=$(wc -c < "$out")
printf 'package: %s — %d files, %d bytes (%d KiB)\n' "$out" "$count" "$size" "$(( (size + 1023) / 1024 ))"
if [[ -d addons/dirge/bin ]]; then
  echo "package: native binaries:"
  find addons/dirge/bin -type f | LC_ALL=C sort | sed 's/^/  /'
else
  echo "package: no native binaries (addons/dirge/bin absent)"
fi
