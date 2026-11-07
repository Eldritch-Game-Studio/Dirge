#!/usr/bin/env bash
# Installs the NecronomiDOM release the tool UI depends on into addons/necronomidom (git-ignored).
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${NECRONOMIDOM_VERSION:-1.1.4}"
URL="https://github.com/Eldritch-Game-Studio/NecronomiDOM/releases/download/v${VERSION}/NecronomiDOM-v${VERSION}.zip"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
echo "fetch-deps: NecronomiDOM v${VERSION}"
curl --fail --location --silent --show-error "$URL" -o "$tmp/nd.zip"
unzip -oq "$tmp/nd.zip" -d "$tmp/nd"
rm -rf addons/necronomidom
cp -r "$tmp/nd/addons/necronomidom" addons/necronomidom
echo "fetch-deps: installed addons/necronomidom ($(grep -m1 -o 'version=\"[^\"]*\"' addons/necronomidom/plugin.cfg 2>/dev/null || echo v${VERSION}))"
