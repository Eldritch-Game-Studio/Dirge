#!/usr/bin/env bash
# M1 T1 smoke: import the project headless, build, run the demo scene in smoke mode with the Dummy audio driver.
# Usage: scripts/smoke.sh            (GODOT env var overrides the editor binary)
set -euo pipefail

cd "$(dirname "$0")/.."

GODOT="${GODOT:-$HOME/.local/opt/godot-stock/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
if [[ ! -x "$GODOT" ]]; then
  echo "smoke: Godot binary not found at $GODOT (set GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64)" >&2
  exit 2
fi

if [[ ! -f demo/audio/wind.wav || ! -f demo/audio/click.wav ]]; then
  python3 scripts/gen-demo-audio.py
fi

echo "smoke: dotnet build"
dotnet build Dirge.slnx -nologo -v q

echo "smoke: godot --import"
# A first import of a fresh checkout can abort in editor shutdown after the assets are already imported; retry once.
if ! "$GODOT" --headless --audio-driver Dummy --path . --import >/tmp/dirge-import.log 2>&1; then
  "$GODOT" --headless --audio-driver Dummy --path . --import >/tmp/dirge-import.log 2>&1 || {
    echo "smoke: import failed"; cat /tmp/dirge-import.log; exit 1; }
fi
echo "smoke: run"
set +e
"$GODOT" --headless --audio-driver Dummy --path . res://demo/demo.tscn -- --smoke 2>&1 | tee /tmp/dirge-smoke.log
status=${PIPESTATUS[0]}
set -e

if grep -q '^DIRGE_SMOKE PASS' /tmp/dirge-smoke.log && [[ $status -eq 0 ]]; then
  echo "smoke: PASS"
  exit 0
fi
echo "smoke: FAIL (exit $status)"
exit 1
