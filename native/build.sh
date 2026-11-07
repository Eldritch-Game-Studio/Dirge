#!/usr/bin/env bash
# Builds the Dirge native GDExtension for Linux x86_64 (debug + release) into
# addons/dirge/bin/linux-x64/. Needs a C++17 compiler and SCons (pip/pipx install scons).
# Usage: native/build.sh [extra scons args, e.g. -j8]
set -euo pipefail

cd "$(dirname "$0")"

if [[ ! -f godot-cpp/SConstruct ]]; then
  echo "build: godot-cpp submodule missing; run: git submodule update --init native/godot-cpp" >&2
  exit 2
fi

SCONS="${SCONS:-}"
if [[ -z "$SCONS" ]]; then
  if command -v scons >/dev/null 2>&1; then
    SCONS=scons
  elif [[ -x "$HOME/.local/bin/scons" ]]; then
    SCONS="$HOME/.local/bin/scons"
  else
    echo "build: scons not found; install with 'pipx install scons' or 'pip install --user scons'" >&2
    exit 2
  fi
fi

# Keep the Godot editor from importing godot-cpp's own test project. The marker is
# untracked inside the submodule, so hide it from its status too.
if [[ ! -f godot-cpp/.gdignore ]]; then
  touch godot-cpp/.gdignore
  exclude="$(git -C godot-cpp rev-parse --git-path info/exclude 2>/dev/null || true)"
  if [[ -n "$exclude" ]] && ! grep -qx '.gdignore' "$exclude" 2>/dev/null; then
    mkdir -p "$(dirname "$exclude")"
    echo '.gdignore' >>"$exclude"
  fi
fi

JOBS="${JOBS:-$(nproc 2>/dev/null || echo 4)}"
for target in template_debug template_release; do
  echo "build: linux x86_64 $target"
  "$SCONS" platform=linux arch=x86_64 target="$target" -j"$JOBS" "$@"
done

echo "build: done"
ls -la ../addons/dirge/bin/linux-x64/
