#!/usr/bin/env bash
# Install the stock .NET distribution into the current job's temporary directory.
set -euo pipefail
: "${GODOT_VERSION:?}" "${RUNNER_TEMP:?}" "${GITHUB_ENV:?}"
case "$(uname -s)" in
  Darwin) platform=macos.universal ;;
  Linux) platform=linux_x86_64 ;;
  *) echo 'unsupported Godot smoke host' >&2; exit 2 ;;
esac
archive="$RUNNER_TEMP/godot-zip/godot.zip"
mkdir -p "$(dirname "$archive")"
if [[ ! -f "$archive" ]]; then
  curl --fail --location --silent --show-error \
    "https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}-stable/Godot_v${GODOT_VERSION}-stable_mono_${platform}.zip" \
    -o "$archive"
fi
destination="$(mktemp -d "$RUNNER_TEMP/dirge-godot.XXXXXX")"
unzip -oq "$archive" -d "$destination"
if [[ "$platform" = macos.universal ]]; then
  godot="$destination/Godot_mono.app/Contents/MacOS/Godot"
else
  godot="$destination/Godot_v${GODOT_VERSION}-stable_mono_linux_x86_64/Godot_v${GODOT_VERSION}-stable_mono_linux.x86_64"
fi
[[ -x "$godot" ]] || { echo "stock Godot .NET executable missing: $godot" >&2; exit 1; }
echo "GODOT=$godot" >> "$GITHUB_ENV"
