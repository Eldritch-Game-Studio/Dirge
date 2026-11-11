#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build tools/dirge-validate -nologo -v q
# POSIX glob handling works with macOS's system Bash; do not require GNU shell tools.
count=0
status=0
for file in demo/*.dirge.json demo/sandbox/*.dirge.json; do
  [[ -f "$file" ]] || continue
  count=$((count + 1))
  echo "::group::$file"
  dotnet run --no-build --project tools/dirge-validate -- "$file" || status=1
  echo '::endgroup::'
done
[[ $count -gt 0 ]] || { echo 'no *.dirge.json files found' >&2; exit 1; }
exit "$status"
