#!/usr/bin/env bash
# Install the exact runtime-only bridge package from dependency-lock.json or explicit pins.
set -euo pipefail
cd "$(dirname "$0")/.."
exec python3 scripts/install-dependency.py "$@"
