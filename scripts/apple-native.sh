#!/usr/bin/env bash
# Invoke through apple-run.py: universal native build and both stock-Godot runtime architectures.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${GODOT:?}" "${RUNNER_TEMP:?}"
touch native/godot-cpp/.gdignore
for target in template_debug template_release; do
  (cd native && scons platform=macos target="$target" arch=universal -j"${JOBS:-4}")
  binary="addons/dirge/bin/macos/libdirge_native.macos.${target}.framework/libdirge_native.macos.${target}"
  for architecture in arm64 x86_64; do
    lipo "$binary" -verify_arch "$architecture"
  done
done
for architecture in arm64 x86_64; do
  lipo "$GODOT" -verify_arch "$architecture"
done
scripts/fetch-deps.sh
for architecture in arm64 x86_64; do
  export GODOT_ARCH="$architecture"
  export DIRGE_SMOKE_LOG_DIR="$RUNNER_TEMP/dirge-smoke-$architecture"
  scripts/smoke.sh
done
