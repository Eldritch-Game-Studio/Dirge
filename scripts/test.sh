#!/usr/bin/env bash
# Source-owned delivery tests; GDScript behavioral suites remain mandatory in smoke.sh.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 -m unittest discover -s tests/scripts -v
