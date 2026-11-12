#!/usr/bin/env bash
# No arguments validates authored demos; explicit arguments expose the standalone CLI.
set -euo pipefail
cd "$(dirname "$0")/.."
GODOT="${GODOT:-$(command -v godot || true)}"
[[ -n "$GODOT" && -x "$GODOT" ]] || { echo 'validate: standard Godot required (set GODOT)' >&2; exit 2; }
godot=("$GODOT")
if [[ -n "${GODOT_ARCH:-}" ]]; then godot=(/usr/bin/arch "-$GODOT_ARCH" "$GODOT"); fi
if [[ $# -gt 0 ]]; then
  exec "${godot[@]}" --headless --no-header --path . --script res://tools/dirge-validate/run.gd -- "$@"
fi
count=0
status=0
for file in demo/*.dirge.json demo/sandbox/*.dirge.json; do
  [[ -f "$file" ]] || continue
  count=$((count + 1))
  echo "::group::$file"
  "${godot[@]}" --headless --no-header --path . --script res://tools/dirge-validate/run.gd -- "$file" || status=1
  echo '::endgroup::'
done
[[ $count -gt 0 ]] || { echo 'no *.dirge.json files found' >&2; exit 1; }
exit "$status"
