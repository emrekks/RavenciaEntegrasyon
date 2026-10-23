#!/usr/bin/env bash
# Ships HEAD: codex/<topic> branch -> Validate -> fast-forward main (only when main is its ancestor) -> live images.
# Read-only on the server (docker inspect). Usage: ship.sh [topic]
set -u
key="${RAVENCIA_SSH_KEY:-C:/Users/emrek/Desktop/İŞ/Ravencia.pem}"
host="${RAVENCIA_SSH_HOST:-ubuntu@63.180.140.51}"
topic="${1:-$(git rev-parse --abbrev-ref HEAD)}"
branch="codex/${topic#codex/}"
full=$(git rev-parse HEAD)
echo "sha=$full branch=$branch"

git push -q origin "HEAD:refs/heads/$branch" || { echo "push to $branch failed"; exit 1; }

# The Validate run of this exact commit (it can take a moment to appear).
id=""
for _ in $(seq 1 30); do
  id=$(gh run list --branch "$branch" --workflow Validate --limit 10 --json databaseId,headSha -q ".[] | select(.headSha==\"$full\") | .databaseId" | head -1)
  [ -n "$id" ] && break
  sleep 10
done
[ -z "$id" ] && { echo "validate run not found"; exit 1; }
gh run watch "$id" --exit-status >/dev/null 2>&1
status=$?
echo "validate=$status"
if [ "$status" != "0" ]; then
  gh run view "$id" --log-failed 2>/dev/null | grep -E "FAIL |AssertionError|\[FAIL\]| error |Error Message" | head -20
  exit 1
fi

git fetch -q origin
if git merge-base --is-ancestor origin/main "$full"; then
  git push -q origin "$full:refs/heads/main" && echo "pushed" || { echo "not pushed"; exit 1; }
else
  echo "not pushed: main moved"
  git log --oneline -3 origin/main
  exit 1
fi

images=""
for _ in $(seq 1 80); do
  images=$(ssh -i "$key" -o BatchMode=yes "$host" "sudo docker inspect -f '{{.Config.Image}}' marketplacehub-caddy-1 marketplacehub-api-1 marketplacehub-worker-1" 2>/dev/null | tr '\n' ' ')
  [ "$(echo "$images" | grep -o "${full:0:12}" | wc -l)" = "3" ] && { echo "live: $images"; exit 0; }
  sleep 30
done
echo "not live yet: $images"
exit 1
