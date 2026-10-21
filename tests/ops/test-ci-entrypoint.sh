#!/usr/bin/env bash
# Decisions of deploy/scripts/ci-entrypoint.sh (the deploy key's forced command),
# run against throwaway git repositories: a bare "origin" and a checkout whose
# deploy/scripts/fast-deploy.sh is a stub that records the deploy like the real
# one (.last-success) and exits with $STUB_EXIT.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
entrypoint="$repository_root/deploy/scripts/ci-entrypoint.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
export HOME="$work/home"
mkdir -p -- "$HOME"
export GIT_AUTHOR_NAME=ops-test GIT_AUTHOR_EMAIL=ops-test@example.invalid
export GIT_COMMITTER_NAME=ops-test GIT_COMMITTER_EMAIL=ops-test@example.invalid
export GIT_CONFIG_NOSYSTEM=1
origin="$work/origin.git"
checkout="$work/checkout"
author="$work/author"
state="$HOME/deploy-logs"

git init --quiet --bare --initial-branch=main "$origin"
git init --quiet --initial-branch=main "$author"
mkdir -p -- "$author/deploy/scripts"
cat > "$author/deploy/scripts/fast-deploy.sh" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
echo "stub deploy of $DEPLOY_REVISION"
if [[ "${STUB_EXIT:-0}" == 0 ]]; then
  printf '%s\n' "$DEPLOY_REVISION" > "$HOME/deploy-logs/.last-success"
fi
exit "${STUB_EXIT:-0}"
STUB
commit() {
  printf '%s\n' "$1" > "$author/version.txt"
  git -C "$author" add --all
  git -C "$author" commit --quiet -m "$1"
  git -C "$author" rev-parse HEAD
}
push() { git -C "$author" push --quiet "$origin" main; }

rev_a="$(commit a)"
push
git clone --quiet "$origin" "$checkout"
rev_b="$(commit b)"
push

# Runs the entrypoint as sshd would; prints its output and "exit=<code>".
deploy() {
  local request="$1" clock="${2:-1200}" status=0 output
  output="$(SSH_ORIGINAL_COMMAND="$request" RAVENCIA_REPOSITORY_ROOT="$checkout" RAVENCIA_DEPLOY_NOW_HHMM="$clock" \
    bash "$entrypoint" 2>&1)" || status=$?
  printf '%s\nexit=%s\n' "$output" "$status"
}

echo "ci-entrypoint decisions"

result="$(deploy "not-a-sha")"
assert_contains "$result" "exit=2" "rejects a request that is not a 40-character SHA"

result="$(deploy "$rev_b" 2350)"
assert_contains "$result" "deferred to morning" "defers a deploy at 23:50 Istanbul"
assert_contains "$result" "exit=0" "a deferred deploy exits 0"
assert_not_contains "$result" "stub deploy" "a deferred deploy does not start fast-deploy.sh"
assert_eq "$rev_a" "$(git -C "$checkout" rev-parse HEAD)" "a deferred deploy leaves the checkout alone"

result="$(deploy "$rev_b" 0015)"
assert_contains "$result" "deferred to morning" "defers a deploy at 00:15 Istanbul"

result="$(deploy "$rev_b")"
assert_contains "$result" "stub deploy of $rev_b" "deploys a newer validated revision"
assert_contains "$result" "exit=0" "a successful deploy exits 0"
assert_eq "$rev_b" "$(git -C "$checkout" rev-parse HEAD)" "fast-forwards the checkout to the revision"
assert_eq "$rev_b" "$(head -c 40 "$state/.last-success")" "records the verified revision"

result="$(deploy "$rev_a")"
assert_contains "$result" "already live" "skips a revision the last verified deploy contains"
assert_not_contains "$result" "stub deploy" "does not redeploy a live revision"

result="$(deploy "$rev_b redeploy")"
assert_contains "$result" "stub deploy of $rev_b" "redeploys a live revision on request"

printf '%s\n' "$rev_b" > "$state/.hold"
result="$(deploy "$rev_b redeploy")"
assert_contains "$result" "is held after a rollback" "refuses a revision held after a rollback"
assert_contains "$result" "exit=0" "a held revision exits 0"

rev_c="$(commit c)"
push
result="$(deploy "$rev_c")"
assert_contains "$result" "stub deploy of $rev_c" "deploys a commit newer than the hold"
assert_contains "$result" "Rollback hold on ${rev_b:0:12} lifted" "a verified newer deploy lifts the hold"
if [[ -e "$state/.hold" ]]; then fail "removes the hold file"; else pass "removes the hold file"; fi

# HEAD is c but the last verified deploy is only a: b is older than HEAD and not live.
printf '%s\n' "$rev_a" > "$state/.last-success"
result="$(deploy "$rev_b")"
assert_contains "$result" "exit=1" "refuses an older revision when the newer deploy did not finish verified"
assert_contains "$result" "rollback.sh" "points to rollback.sh"

rev_d="$(commit d)"
push
result="$(STUB_EXIT=3 deploy "$rev_d")"
assert_contains "$result" "exit=3" "passes on a failed deploy's exit code"
assert_eq "$rev_a" "$(head -c 40 "$state/.last-success")" "a failed deploy does not move .last-success"

finish "ci-entrypoint decisions"
