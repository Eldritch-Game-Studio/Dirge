#!/usr/bin/env bash
# Headless smoke: import the project, build, run the demo scene and the sandbox scene in smoke mode with the Dummy audio driver.
# Usage: GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64 scripts/smoke.sh   (or have `godot` on PATH)
set -euo pipefail

cd "$(dirname "$0")/.."

GODOT="${GODOT:-$(command -v godot || true)}"
if [[ -z "$GODOT" || ! -x "$GODOT" ]]; then
  echo "smoke: Godot .NET editor binary not found (set GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64 or put 'godot' on PATH)" >&2
  exit 2
fi

if [[ ! -d addons/necronomidom ]]; then
  scripts/fetch-deps.sh
fi

if [[ ! -f demo/audio/wind.wav || ! -f demo/audio/click.wav ]]; then
  python3 scripts/gen-demo-audio.py
fi

if [[ ! -f demo/sandbox/audio/calm_pad.wav || ! -f demo/sandbox/audio/line_de.wav ]]; then
  python3 scripts/gen-sandbox-audio.py
fi

echo "smoke: dotnet build"
dotnet build Dirge.slnx -nologo -v q

echo "smoke: godot --import"
# A first import of a fresh checkout can abort in editor shutdown after the assets are already imported; retry once.
if ! "$GODOT" --headless --audio-driver Dummy --path . --import >/tmp/dirge-import.log 2>&1; then
  "$GODOT" --headless --audio-driver Dummy --path . --import >/tmp/dirge-import.log 2>&1 || {
    echo "smoke: import failed"; cat /tmp/dirge-import.log; exit 1; }
fi
# The plugin must leave an existing project's settings as authored (Godot normalizes project.godot on every save).
if git rev-parse --is-inside-work-tree >/dev/null 2>&1 && ! git diff --quiet -- project.godot; then
  echo "smoke: import rewrote project.godot"; git --no-pager diff -- project.godot; exit 1
fi
echo "smoke: run demo"
set +e
"$GODOT" --headless --audio-driver Dummy --path . res://demo/demo.tscn -- --smoke 2>&1 | tee /tmp/dirge-smoke.log
status=${PIPESTATUS[0]}
set -e

if grep -q '^DIRGE_SMOKE PASS' /tmp/dirge-smoke.log && [[ $status -eq 0 ]]; then
  echo "smoke: demo PASS"
else
  echo "smoke: demo FAIL (exit $status)"
  exit 1
fi

echo "smoke: run sandbox"
set +e
"$GODOT" --headless --audio-driver Dummy --path . res://demo/sandbox/sandbox.tscn -- --smoke 2>&1 | tee /tmp/dirge-sandbox-smoke.log
status=${PIPESTATUS[0]}
set -e

if grep -q '^SANDBOX_SMOKE PASS' /tmp/dirge-sandbox-smoke.log && [[ $status -eq 0 ]]; then
  echo "smoke: sandbox PASS"
else
  echo "smoke: sandbox FAIL (exit $status)"
  exit 1
fi

echo "smoke: PASS"
