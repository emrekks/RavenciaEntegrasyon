#!/usr/bin/env bash
# Ships HEAD: codex/<topic> branch -> Validate -> fast-forward main (only when main is its ancestor) -> live images.
# Many sessions ship at once and main moves faster than a Validate run, so the step that reaches main is queued:
# one ship at a time holds a lock shared by every worktree of this repository. Holding it, a ship whose main moved
# merges origin/main, validates that merge and pushes it; no other ship can move main meanwhile.
# Read-only on the server (docker inspect). Usage: ship.sh [topic]
set -u
key="${RAVENCIA_SSH_KEY:-C:/Users/emrek/Desktop/İŞ/Ravencia.pem}"
host="${RAVENCIA_SSH_HOST:-ubuntu@63.180.140.51}"
topic="${1:-$(git rev-parse --abbrev-ref HEAD)}"
branch="codex/${topic#codex/}"
lock="$(git rev-parse --path-format=absolute --git-common-dir)/ship.lock"
lock_stale_min=60
full=$(git rev-parse HEAD)
echo "sha=$full branch=$branch"

git diff --quiet && git diff --cached --quiet || { echo "uncommitted changes: commit first"; exit 1; }

# Pushes $full to the codex branch and waits for its Validate run. Returns the run's exit status.
# Many ships poll GitHub at once and share one API rate limit, so it asks the REST API directly, once every 30 s
# (`gh run list` / `gh run watch` also fetch the workflow list on every call), and a poll that fails on an API error
# is retried instead of being taken for a failed Validate.
validate() {
  git push -q origin "$full:refs/heads/$branch" || { echo "push to $branch failed"; return 1; }
  local id="" status conclusion="" out
  # On an API error gh prints the error body to stdout, so only a well-formed answer counts.
  for _ in $(seq 1 20); do
    out=$(gh api "repos/{owner}/{repo}/actions/runs?branch=$branch&head_sha=$full&per_page=10" \
      -q '.workflow_runs[] | select(.name=="Validate") | .id' 2>/dev/null) && id=$(echo "$out" | head -1)
    [[ "$id" =~ ^[0-9]+$ ]] && break
    id=""
    sleep 30
  done
  [ -z "$id" ] && { echo "validate run not found"; return 1; }
  # Up to 90 minutes.
  for _ in $(seq 1 180); do
    out=$(gh api "repos/{owner}/{repo}/actions/runs/$id" -q 'select(.status=="completed") | .conclusion' 2>/dev/null) \
      && [[ "$out" =~ ^[a-z_]+$ ]] && { conclusion=$out; break; }
    sleep 30
  done
  [ "$conclusion" = "success" ] && status=0 || status=1
  echo "validate=$status${conclusion:+ ($conclusion)}"
  [ -z "$conclusion" ] && { echo "validate result unknown: no answer from GitHub"; return 1; }
  [ "$status" = "0" ] && return 0
  gh run view "$id" --log-failed 2>/dev/null | grep -E "FAIL |AssertionError|\[FAIL\]| error |Error Message" | head -20
  return 1
}

held=""
release() { [ -n "$held" ] && rm -rf "$lock"; held=""; }
trap release EXIT
trap 'release; exit 130' INT TERM

acquire() {
  local waited=0
  until mkdir "$lock" 2>/dev/null; do
    if [ -n "$(find "$lock" -maxdepth 0 -mmin +"$lock_stale_min" 2>/dev/null)" ]; then
      echo "removing stale ship lock: $(cat "$lock/owner" 2>/dev/null)"
      rm -rf "$lock"
      continue
    fi
    [ $((waited % 300)) = 0 ] && echo "waiting for ship lock: $(cat "$lock/owner" 2>/dev/null)"
    sleep 15
    waited=$((waited + 15))
  done
  held=1
  echo "$branch $(date -u +%FT%TZ)" > "$lock/owner"
}

# 1. Validate the commit as it is, in parallel with other ships, so a failing test shows up without queueing.
validate || exit 1

# 2. Queue for main.
acquire
git fetch -q origin
if ! git merge-base --is-ancestor origin/main "$full"; then
  echo "main moved: merging origin/main"
  git log --oneline -3 origin/main
  if ! git merge --no-edit -q origin/main; then
    git merge --abort
    echo "not pushed: merge conflict with main, resolve it and ship again"
    exit 1
  fi
  full=$(git rev-parse HEAD)
  echo "sha=$full"
  validate || { echo "not pushed: the merge with main failed Validate"; exit 1; }
  git fetch -q origin
  git merge-base --is-ancestor origin/main "$full" || { echo "not pushed: main moved outside ship"; git log --oneline -3 origin/main; exit 1; }
fi
git push -q origin "$full:refs/heads/main" && echo "pushed" || { echo "not pushed"; exit 1; }
release

images=""
for _ in $(seq 1 80); do
  images=$(ssh -i "$key" -o BatchMode=yes "$host" "sudo docker inspect -f '{{.Config.Image}}' marketplacehub-caddy-1 marketplacehub-api-1 marketplacehub-worker-1" 2>/dev/null | tr '\n' ' ')
  [ "$(echo "$images" | grep -o "${full:0:12}" | wc -l)" = "3" ] && { echo "live: $images"; exit 0; }
  sleep 30
done
echo "not live yet: $images"
exit 1
