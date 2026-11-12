#!/usr/bin/env bash
# Local Linux diagnostic: no GitHub runner, approval token, or hosted compute.
# Full release qualification still requires the local Apple and Linux CI producers.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname -s)" = Linux ]] || { echo 'ci-local.sh is the Linux diagnostic path' >&2; exit 2; }
[[ $# = 0 ]] || { echo 'usage: scripts/ci-local.sh' >&2; exit 2; }
python3 -m unittest discover -s tests/scripts -v
native/build.sh
scripts/fetch-deps.sh
scripts/smoke.sh
scripts/validate.sh
scripts/package.sh --suffix -partial
