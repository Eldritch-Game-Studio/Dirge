#!/usr/bin/env bash
# Headless smoke: import the project, build, run the demo scene and the sandbox scene in smoke mode with the Dummy audio driver.
# Usage: GODOT=/path/to/the/stock/Godot.NET/executable scripts/smoke.sh
set -euo pipefail

cd "$(dirname "$0")/.."

GODOT="${GODOT:-$(command -v godot || true)}"
if [[ -z "$GODOT" || ! -x "$GODOT" ]]; then
  echo "smoke: Godot .NET editor binary not found (set GODOT to the stock executable or put 'godot' on PATH)" >&2
  exit 2
fi

godot=("$GODOT")
if [[ -n "${GODOT_ARCH:-}" ]]; then
  godot=(/usr/bin/arch "-$GODOT_ARCH" "$GODOT")
fi
log_dir="${DIRGE_SMOKE_LOG_DIR:-${TMPDIR:-/tmp}/dirge-smoke}"
mkdir -p "$log_dir"

# Idempotent: keeps a verified install of the locked (or explicitly supplied candidate) NecronomiDOM, replaces anything else.
scripts/fetch-deps.sh

if [[ ! -f demo/audio/wind.wav || ! -f demo/audio/click.wav ]]; then
  python3 scripts/gen-demo-audio.py
fi

if [[ ! -f demo/sandbox/audio/calm_pad.wav || ! -f demo/sandbox/audio/line_de.wav ]]; then
  python3 scripts/gen-sandbox-audio.py
fi

echo "smoke: dotnet build (native host SDK)"
# Every platform shares a host with other producers. Keep this build in one
# process and never attach to reusable MSBuild/Roslyn servers owned by other work.
dotnet build Dirge.slnx -nologo -v normal --disable-build-servers \
  -m:1 -nr:false -p:UseSharedCompilation=false \
  "-bl:$log_dir/managed-build.binlog" 2>&1 | tee "$log_dir/managed-build.log"

echo "smoke: godot --import"
# Native smoke retains its editor-shutdown retry. Compatibility qualification
# fails on its first import error; never overwrite or hide the failing oracle.
if ! "${godot[@]}" --headless --audio-driver Dummy --path . --import >"$log_dir/import.log" 2>&1; then
  if [[ "${DIRGE_SMOKE_ISOLATED_BUILD:-0}" = 1 ]]; then
    echo "smoke: compatibility import failed"; cat "$log_dir/import.log"; exit 1
  fi
  "${godot[@]}" --headless --audio-driver Dummy --path . --import >"$log_dir/import.log" 2>&1 || {
    echo "smoke: import failed"; cat "$log_dir/import.log"; exit 1; }
fi
# The plugin must leave an existing project's settings as authored (Godot normalizes project.godot on every save).
if git rev-parse --is-inside-work-tree >/dev/null 2>&1 && ! git diff --quiet -- project.godot; then
  echo "smoke: import rewrote project.godot"; git --no-pager diff -- project.godot; exit 1
fi
echo "smoke: run demo"
set +e
"${godot[@]}" --headless --audio-driver Dummy --path . res://demo/demo.tscn -- --smoke 2>&1 | tee "$log_dir/demo.log"
status=${PIPESTATUS[0]}
set -e

if grep -q '^DIRGE_SMOKE PASS' "$log_dir/demo.log" && [[ $status -eq 0 ]]; then
  echo "smoke: demo PASS"
else
  echo "smoke: demo FAIL (exit $status)"
  exit 1
fi

# The spatial smoke's engine probe decides whether native pending-pause assertions were enforced or skipped.
pending_pause="$(grep -m1 -oE '^DIRGE_NATIVE_PENDING_PAUSE engine .*' "$log_dir/demo.log" | sed 's/^DIRGE_NATIVE_PENDING_PAUSE //')" || true
if [[ -z "$pending_pause" ]]; then
  echo "smoke: demo did not report its native pending-pause probe result"
  exit 1
fi
echo "smoke: $pending_pause"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  platform="$(uname -s)${GODOT_ARCH:+ $GODOT_ARCH}"
  [[ -z "${DIRGE_WINDOWS_GODOT:-}" ]] || platform="Windows under Proton"
  echo "- Dirge spatial smoke ($platform): $pending_pause" >> "$GITHUB_STEP_SUMMARY"
fi
if [[ "${GITHUB_ACTIONS:-}" = true ]]; then
  if [[ "$pending_pause" = *lacks* ]]; then echo "::warning title=Native pending pause::$pending_pause"
  else echo "::notice title=Native pending pause::$pending_pause"; fi
fi

echo "smoke: run sandbox"
set +e
"${godot[@]}" --headless --audio-driver Dummy --path . res://demo/sandbox/sandbox.tscn -- --smoke 2>&1 | tee "$log_dir/sandbox.log"
status=${PIPESTATUS[0]}
set -e

if grep -q '^SANDBOX_SMOKE PASS' "$log_dir/sandbox.log" && [[ $status -eq 0 ]]; then
  echo "smoke: sandbox PASS"
else
  echo "smoke: sandbox FAIL (exit $status)"
  exit 1
fi

echo "smoke: PASS"
