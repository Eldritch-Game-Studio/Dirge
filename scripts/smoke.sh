#!/usr/bin/env bash
# Import and exercise the real GDScript suite, both demos, and native audio tests.
# Usage: GODOT=/path/to/standard/godot scripts/smoke.sh
set -euo pipefail
cd "$(dirname "$0")/.."
GODOT="${GODOT:-$(command -v godot || true)}"
export GODOT
[[ -n "$GODOT" && -x "$GODOT" ]] || { echo 'smoke: standard Godot executable required (set GODOT)' >&2; exit 2; }
godot=("$GODOT")
if [[ -n "${GODOT_ARCH:-}" ]]; then godot=(/usr/bin/arch "-$GODOT_ARCH" "$GODOT"); fi
version="$("${godot[@]}" --version)"
if ! printf '%s\n' "$version" | grep -Eq '^4\.7\.2\.stable(\.|$)' || printf '%s\n' "$version" | grep -Eq '^4\.7\.2\..*mono'; then
  echo "smoke: standard Godot 4.7.2 required, got $version" >&2; exit 2
fi
log_dir="${DIRGE_SMOKE_LOG_DIR:-${TMPDIR:-/tmp}/dirge-smoke}"
mkdir -p "$log_dir"
# Never restore another project's editor session or write the user's editor preferences.
export XDG_CONFIG_HOME="$log_dir/user/config"
export XDG_DATA_HOME="$log_dir/user/data"
export XDG_CACHE_HOME="$log_dir/user/cache"
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"
scripts/fetch-deps.sh
if [[ ! -f demo/audio/wind.wav || ! -f demo/audio/click.wav ]]; then python3 scripts/gen-demo-audio.py; fi
if [[ ! -f demo/sandbox/audio/calm_pad.wav || ! -f demo/sandbox/audio/line_de.wav ]]; then python3 scripts/gen-sandbox-audio.py; fi
settings="$(mktemp)"
trap 'rm -f "$settings"' EXIT
cp project.godot "$settings"
run_checked() {
  local name="$1" marker="$2"
  shift 2
  "${godot[@]}" --headless --audio-driver Dummy --path . "$@" 2>&1 | tee "$log_dir/$name.log"
  if grep -Eq '^(ERROR:|SCRIPT ERROR:)' "$log_dir/$name.log"; then
    echo "smoke: $name reported engine/script errors" >&2; exit 1
  fi
  if [[ -n "$marker" ]] && ! grep -Eq "^${marker}([[:space:]]|$)" "$log_dir/$name.log"; then
    echo "smoke: $name missing $marker" >&2; exit 1
  fi
}
run_checked import '' --import
cmp -s project.godot "$settings" || { echo 'smoke: import rewrote project.godot' >&2; exit 1; }
run_checked gdscript 'DIRGE_TEST PASS' --script res://tests/gdscript/run.gd
run_checked demo 'DIRGE_SMOKE PASS' res://demo/demo.tscn -- --smoke
run_checked sandbox 'SANDBOX_SMOKE PASS' res://demo/sandbox/sandbox.tscn -- --smoke
run_checked native 'NATIVE_TEST PASS' res://native/tests/sendreturn.tscn
# Proton captures a separate Windows engine log; its wrapper is not a JSON stdout transport.
# The CLI JSON contract is exercised directly on both native Unix hosts.
if [[ -z "${DIRGE_PROTON_WORK:-}" ]]; then
  python3 -m unittest discover -s tests/scripts -p test_validate_cli.py -v 2>&1 | tee "$log_dir/validator-cli.log"
fi
echo 'smoke: PASS'
