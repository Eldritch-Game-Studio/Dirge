#!/usr/bin/env bash
# Installs the NecronomiDOM managed-host ZIP pinned by dependency-lock.json into addons/necronomidom (git-ignored).
# The ZIP's SHA-256, its addon-package.json identity and every per-file hash are verified; there is no version fallback.
# Qualifying a candidate instead of the lock requires NECRONOMIDOM_PACKAGE_URL, NECRONOMIDOM_PACKAGE_SHA256 and
# NECRONOMIDOM_REVISION together (see scripts/install-dependency.py).
set -euo pipefail
cd "$(dirname "$0")/.."
exec python3 scripts/install-dependency.py
