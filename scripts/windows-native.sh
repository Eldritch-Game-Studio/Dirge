#!/usr/bin/env bash
# Cross-build the existing Windows GDExtension ABI on the local Linux producer.
set -euo pipefail
cd "$(dirname "$0")/.."
SCONS="${SCONS:-$(command -v scons || printf '%s' "$HOME/.local/bin/scons")}"
command -v x86_64-w64-mingw32-g++ >/dev/null
command -v x86_64-w64-mingw32-objdump >/dev/null
# Never retain qualification for binaries about to be rebuilt.
rm -f addons/dirge/bin/windows-x64/proton-qualification.json
for target in template_debug template_release; do
  (cd native && "$SCONS" platform=windows target="$target" arch=x86_64 use_mingw=yes use_static_cpp=yes -j"${JOBS:-4}")
  binary="addons/dirge/bin/windows-x64/libdirge_native.windows.${target}.x86_64.dll"
  headers="$(x86_64-w64-mingw32-objdump -p "$binary")"
  printf '%s\n' "$headers"
  [[ "$headers" = *'file format pei-x86-64'* ]]
  printf '%s\n' "$headers" | grep -E '[[:space:]]dirge_native_init$' >/dev/null
  if printf '%s\n' "$headers" | grep -Ei 'DLL Name: (libgcc|libstdc\+\+|libwinpthread)' >/dev/null; then
    echo 'Windows DLL depends on an unbundled MinGW runtime' >&2
    exit 1
  fi
done
