#!/usr/bin/env bash
# Invoke through apple-run.py: universal native build and both stock-Godot runtime architectures.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${GODOT:?}" "${DOTNET_ROOT_X64:?}"
touch native/godot-cpp/.gdignore
for target in template_debug template_release; do
  (cd native && scons platform=macos target="$target" arch=universal -j"${JOBS:-4}")
  binary="addons/dirge/bin/macos/libdirge_native.macos.${target}.framework/libdirge_native.macos.${target}.dylib"
  lipo -verify_arch arm64 x86_64 "$binary"
done
lipo -verify_arch arm64 x86_64 "$GODOT"
scripts/fetch-deps.sh
for architecture in arm64 x86_64; do
  export GODOT_ARCH="$architecture"
  export DIRGE_SMOKE_LOG_DIR="$RUNNER_TEMP/dirge-smoke-$architecture"
  # Godot's hostfxr resolver uses the matching per-architecture runtime root.
  if [[ "$architecture" = x86_64 ]]; then
    export DOTNET_ROOT="$DOTNET_ROOT_X64"
    export PATH="$DOTNET_ROOT_X64:$PATH"
  fi
  scripts/smoke.sh
  /usr/bin/arch -"$architecture" "$GODOT" --headless --audio-driver Dummy --path . \
    res://native/tests/sendreturn.tscn 2>&1 | tee "$DIRGE_SMOKE_LOG_DIR/native.log"
  grep -q '^NATIVE_TEST PASS' "$DIRGE_SMOKE_LOG_DIR/native.log"
done
