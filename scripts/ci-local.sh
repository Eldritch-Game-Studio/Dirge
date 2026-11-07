#!/usr/bin/env bash
# Runs .github/workflows/ci.yml on this machine with act (https://github.com/nektos/act) in Docker.
#
#   scripts/ci-local.sh            test, validate, smoke, Linux native build, partial addon ZIP in dist/
#   scripts/ci-local.sh -j test    any extra arguments go to act (e.g. a single job)
#
# Only the Linux jobs can run locally: act has no Windows or macOS runners, so native-windows/native-macos are skipped
# and the ZIP contains the Linux library only, named *-partial.zip. Releases still come from the GitHub workflow.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
ACT="${ACT:-$(command -v act || echo "$HOME/.local/bin/act")}"
[[ -x "$ACT" ]] || { echo "act not found: install it (https://nektosact.com) or set ACT=/path/to/act" >&2; exit 2; }
ARTIFACTS="$ROOT/.act/artifacts"
rm -rf "$ARTIFACTS" && mkdir -p "$ARTIFACTS" dist

"$ACT" workflow_dispatch \
  --workflows .github/workflows/ci.yml \
  --input partial=true \
  -P ubuntu-24.04=catthehacker/ubuntu:act-24.04 \
  --artifact-server-path "$ARTIFACTS" \
  "$@"

# act stores each uploaded artifact as a ZIP of its files; unwrap the addon ZIP into dist/.
for artifact in "$ARTIFACTS"/*/Dirge-v*/*.zip "$ARTIFACTS"/*/Dirge-v*.zip; do
  [[ -f "$artifact" ]] || continue
  unzip -oq "$artifact" -d dist
done
ls -la dist/*.zip 2>/dev/null || echo "No addon ZIP produced (package job did not run)."
