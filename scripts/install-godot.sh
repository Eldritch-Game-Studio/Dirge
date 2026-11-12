#!/usr/bin/env bash
# Install the standard (non-.NET) Godot distribution.
set -euo pipefail
: "${GODOT_VERSION:?}" "${RUNNER_TEMP:?}" "${GITHUB_ENV:?}"
case "$(uname -s)" in
  Darwin) platform=macos.universal ;;
  Linux) [[ "$(uname -m)" = x86_64 ]] || { echo 'Linux x86_64 required' >&2; exit 2; }; platform=linux.x86_64 ;;
  *) echo 'unsupported Godot smoke host' >&2; exit 2 ;;
esac
archive="$RUNNER_TEMP/godot-zip/godot-${GODOT_VERSION}-${platform}-standard.zip"
mkdir -p "$(dirname "$archive")"
if [[ ! -f "$archive" ]]; then
  curl --fail --location --silent --show-error \
    "https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}-stable/Godot_v${GODOT_VERSION}-stable_${platform}.zip" \
    -o "$archive"
fi
destination="$(mktemp -d "$RUNNER_TEMP/dirge-godot.XXXXXX")"
unzip -oq "$archive" -d "$destination"
if [[ "$platform" = macos.universal ]]; then
  godot="$destination/Godot.app/Contents/MacOS/Godot"
else
  godot="$destination/Godot_v${GODOT_VERSION}-stable_linux.x86_64"
fi
chmod +x "$godot"
[[ -x "$godot" ]] || { echo "standard Godot executable missing: $godot" >&2; exit 1; }
echo "GODOT=$godot" >> "$GITHUB_ENV"
