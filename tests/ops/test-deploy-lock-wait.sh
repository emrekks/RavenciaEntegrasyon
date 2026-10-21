#!/usr/bin/env bash
# deploy/scripts/wait-for-deploy-lock.sh (ExecStop of ravencia-deploy-guard.service).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
script="$repository_root/deploy/scripts/wait-for-deploy-lock.sh"

work="$(mktemp -d)"
holder=""
cleanup() {
  [[ -z "$holder" ]] || kill "$holder" 2>/dev/null || true
  rm -rf -- "$work"
}
trap cleanup EXIT
lock="$work/.lock"

run_wait() { RAVENCIA_DEPLOY_LOCK="$lock" RAVENCIA_DEPLOY_LOCK_WAIT_SECONDS="$1" bash "$script" 2>&1; }

echo "deploy lock wait"

assert_contains "$(run_wait 5)" "nothing to wait for" "returns at once without a lock file"
if [[ -e "$lock" ]]; then fail "does not create the lock file"; else pass "does not create the lock file"; fi

: > "$lock"
assert_contains "$(run_wait 5)" "No deploy is running" "returns at once when the lock is free"

flock "$lock" sleep 2 &
holder=$!
sleep 0.5
started="$(date +%s)"
output="$(run_wait 20)"
assert_contains "$output" "The deploy finished" "waits until a running deploy releases the lock"
if (( $(date +%s) - started < 15 )); then
  pass "stops waiting as soon as the lock is released"
else
  fail "stops waiting as soon as the lock is released"
fi
wait "$holder" || true

flock "$lock" sleep 10 &
holder=$!
sleep 0.5
output="$(run_wait 1)"
assert_contains "$output" "shutdown continues anyway" "gives up after the configured wait"
kill "$holder" 2>/dev/null || true
wait "$holder" 2>/dev/null || true
holder=""

finish "deploy lock wait"
