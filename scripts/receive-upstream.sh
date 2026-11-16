#!/usr/bin/env bash
# Receiver for `upstream-package-published` from NecronomiDOM (.github/workflows/upstream-receiver.yml).
# Verifies the CI-published managed-host package (scripts/update-dependency-lock.py), then keeps exactly one PR from
# upstream/necronomidom into managed/1.0.x whose only change is dependency-lock.json, and enables auto-merge on it:
# the PR merges only after this line's full qualification (`local / qualification`, a required check) passes.
# Newer packages replace the PR's lock (coalescing); older or already-locked packages change nothing.
# Env: PACKAGE SHA RELEASE_TAG ZIP_SHA256 (the dispatch payload). Requires the runner host's own gh login, because
# pushes and PRs made with the workflow's GITHUB_TOKEN never trigger the qualification they must wait for.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${PACKAGE:?}" "${SHA:?}" "${RELEASE_TAG:?}" "${ZIP_SHA256:?}"

repo=Eldritch-Game-Studio/Dirge
base=managed/1.0.x
branch=upstream/necronomidom
if [[ "$PACKAGE" != necronomidom ]]; then
  echo "receiver: Dirge $base consumes only necronomidom, not '$PACKAGE'" >&2; exit 1
fi
if ! [[ "$SHA" =~ ^[0-9a-f]{40}$ && "$ZIP_SHA256" =~ ^[0-9a-f]{64}$ && "$RELEASE_TAG" == "managed-${SHA:0:12}" ]]; then
  echo "receiver: malformed payload sha=$SHA release_tag=$RELEASE_TAG zip_sha256=$ZIP_SHA256" >&2; exit 1
fi
host() { env -u GH_TOKEN -u GITHUB_TOKEN "$@"; }
work="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/dirge-receiver.$$"
mkdir -p "$work"
# Run outside any condition so a verification failure aborts the receiver (set -e); then ask whether it advanced.
verify() {
  host python3 scripts/update-dependency-lock.py --package "$PACKAGE" --sha "$SHA" --release-tag "$RELEASE_TAG" \
    --zip-sha256 "$ZIP_SHA256" > "$work/$1.json"
  cat "$work/$1.json"
}
advanced() { python3 -c 'import json,sys; sys.exit(0 if json.load(open(sys.argv[1]))["changed"] else 1)' "$work/$1.json"; }
locked_sha() { python3 -c 'import json,sys; print(json.load(sys.stdin)["necronomidom"]["sha"])'; }

host git fetch --quiet origin "+refs/heads/$base:refs/remotes/origin/$base"
pr="$(host gh pr list --repo "$repo" --base "$base" --head "$branch" --state open --json number --jq '.[0].number // empty')"
git checkout --quiet -B "$branch" "origin/$base"

# 1. Against the line itself: an older or already-locked package needs no PR.
verify base
if ! advanced base; then
  echo "receiver: $base already locks NecronomiDOM at or after $SHA"
  if [[ -n "$pr" ]]; then
    host git fetch --quiet origin "+refs/heads/$branch:refs/remotes/origin/$branch"
    pending="$(git show "origin/$branch:dependency-lock.json" | locked_sha)"
    current="$(git show "origin/$base:dependency-lock.json" | locked_sha)"
    status="$(host gh api "repos/Eldritch-Game-Studio/NecronomiDOM/compare/$current...$pending" --jq .status)"
    if [[ "$status" != ahead ]]; then
      host gh pr close "$pr" --repo "$repo" --delete-branch=false \
        --comment "Superseded: $base already locks NecronomiDOM $current."
    fi
  fi
  exit 0
fi

# 2. Against the open PR: never replace a newer pending lock with this one.
if [[ -n "$pr" ]]; then
  host git fetch --quiet origin "+refs/heads/$branch:refs/remotes/origin/$branch"
  git show "origin/$branch:dependency-lock.json" > dependency-lock.json
  verify pending
  if ! advanced pending; then
    git checkout --quiet -- dependency-lock.json
    echo "receiver: PR #$pr already locks NecronomiDOM at or after $SHA"
    host gh pr merge "$pr" --repo "$repo" --auto --merge
    exit 0
  fi
fi

previous="$(git show "origin/$base:dependency-lock.json" | locked_sha)"
git -c user.name="Dirge upstream receiver" -c user.email="dirge-receiver@users.noreply.github.com" \
  commit --quiet -m "Lock NecronomiDOM $RELEASE_TAG" \
  -m "CI-published managed-host package of Eldritch-Game-Studio/NecronomiDOM@$SHA (zip sha256 $ZIP_SHA256), received from upstream-package-published." \
  -- dependency-lock.json
head="$(git rev-parse HEAD)"
host git -c credential.helper= -c 'credential.helper=!gh auth git-credential' \
  push --quiet --force origin "HEAD:refs/heads/$branch"

title="Lock NecronomiDOM $RELEASE_TAG"
body="$(cat <<EOF
Automated by the upstream receiver (run ${GITHUB_SERVER_URL:-https://github.com}/$repo/actions/runs/${GITHUB_RUN_ID:-local}).

| | |
|---|---|
| Package | NecronomiDOM managed-host, [\`$RELEASE_TAG\`](https://github.com/Eldritch-Game-Studio/NecronomiDOM/releases/tag/$RELEASE_TAG) |
| Commit | Eldritch-Game-Studio/NecronomiDOM@$SHA |
| ZIP SHA-256 | \`$ZIP_SHA256\` |
| Previous lock | Eldritch-Game-Studio/NecronomiDOM@$previous |

The receiver verified the release provenance, the ZIP checksum, its \`addon-package.json\` manifest and per-file hashes,
that the commit is on NecronomiDOM main, and that the CI run named in the provenance built it.
This PR auto-merges only when \`local / qualification\` passes against this lock (tests, validation, smoke,
UI document proof and goldens, all native targets). A newer NecronomiDOM package replaces this lock in place.
EOF
)"
if [[ -n "$pr" ]]; then
  host gh pr edit "$pr" --repo "$repo" --title "$title" --body "$body"
else
  host gh pr create --repo "$repo" --base "$base" --head "$branch" --title "$title" --body "$body"
  pr="$(host gh pr list --repo "$repo" --base "$base" --head "$branch" --state open --json number --jq '.[0].number')"
fi
host gh pr merge "$pr" --repo "$repo" --auto --merge --match-head-commit "$head"
echo "receiver: PR #$pr locks NecronomiDOM $SHA at $head; auto-merge waits for qualification"
