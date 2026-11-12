#!/usr/bin/env bash
# Default: all six native libraries plus GDScript/UI/license structural closure.
# --suffix -partial: explicit diagnostic; --require-qualification: current-source Proton gate.
# Existing archives remain untouched unless this exact version is rebuilt successfully.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "${1:-}" = --suffix && "${2:-}" = -partial ]]; then
  shift 2
  set -- --suffix=-partial "$@"
fi
exec python3 scripts/package.py "$@"
