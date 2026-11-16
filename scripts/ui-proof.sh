#!/usr/bin/env bash
# Headless tool-UI proof: mounts every addons/dirge/tool/ui document and the in-game overlay in real NecronomiDOM
# DocumentHosts against the locked dependency, fails on any layout/paint exception or engine error, and compares each
# document's paint runs with its PaintRun-JSON golden in tests/godot/golden/.
# Usage: GODOT=/path/to/stock/Godot.NET scripts/ui-proof.sh [--update-goldens]
# Run after scripts/smoke.sh (which installs the dependency, builds and imports); a mismatch writes actual snapshots to
# $DIRGE_SMOKE_LOG_DIR/ui-proof-actual/.
set -euo pipefail
cd "$(dirname "$0")/.."

GODOT="${GODOT:-$(command -v godot || true)}"
if [[ -z "$GODOT" || ! -x "$GODOT" ]]; then
  echo "ui-proof: Godot .NET editor binary not found (set GODOT to the stock executable or put 'godot' on PATH)" >&2
  exit 2
fi
update=()
case "${1:-}" in
  '') ;;
  --update-goldens) update=(--update-goldens) ;;
  *) echo "usage: scripts/ui-proof.sh [--update-goldens]" >&2; exit 2 ;;
esac
log_dir="${DIRGE_SMOKE_LOG_DIR:-${TMPDIR:-/tmp}/dirge-smoke}"
mkdir -p "$log_dir"

set +e
"$GODOT" --headless --audio-driver Dummy --path . res://tests/godot/ui_document_proof.tscn -- \
  "--out=$log_dir/ui-proof-actual" "${update[@]}" 2>&1 | tee "$log_dir/ui-proof.log"
status=${PIPESTATUS[0]}
set -e
if [[ $status -eq 0 ]] && grep -q '^DIRGE_UI_PROOF PASS' "$log_dir/ui-proof.log"; then
  echo "ui-proof: PASS"
else
  echo "ui-proof: FAIL (exit $status)"
  exit 1
fi
