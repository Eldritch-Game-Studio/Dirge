#!/usr/bin/env bash
# Local Linux diagnostic: no GitHub runner, approval token, or hosted compute.
# Full release qualification still requires the local Apple and Linux CI producers.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname -s)" = Linux ]] || { echo 'ci-local.sh is the Linux diagnostic path' >&2; exit 2; }
[[ $# = 0 ]] || { echo 'usage: scripts/ci-local.sh' >&2; exit 2; }
dotnet test tests/Dirge.Model.Tests -nologo
python3 -m unittest discover -s tests/scripts -v
scripts/validate.sh
native/build.sh
scripts/fetch-deps.sh
scripts/smoke.sh
godot="${GODOT:-$(command -v godot)}"
log_dir="${DIRGE_SMOKE_LOG_DIR:-${TMPDIR:-/tmp}/dirge-smoke}"
"$godot" --headless --audio-driver Dummy --path . res://native/tests/sendreturn.tscn 2>&1 | tee "$log_dir/native.log"
grep -q '^NATIVE_TEST PASS' "$log_dir/native.log"
scripts/package.sh --suffix -partial
