#!/usr/bin/env bash
# docs/patches/*.patch are the owner's prepared, NOT applied, host-side changes (docs/RUNBOOK-GERI-ALMA.md sections 7-9 and
# 10): each one must still apply cleanly to the tree, and the tree must not carry it yet. When a change to the same files
# makes one stale, regenerate it (apply it with `git apply --3way`, fix, `git diff > <patch>`, check the files out again).
# When the owner applies one, the commit that applies it deletes the patch file and updates its runbook section.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

echo "owner patches"

shopt -s nullglob
patches=("$repository_root"/docs/patches/*.patch)
if ((${#patches[@]})); then pass "there are prepared patches"; else fail "there are prepared patches"; fi
for patch in "${patches[@]}"; do
  name="${patch##*/}"
  status=0
  output="$(git -C "$repository_root" apply --check "$patch" 2>&1)" || status=$?
  assert_eq "0" "$status" "$name still applies ($output)"
  status=0
  git -C "$repository_root" apply --check --reverse "$patch" >/dev/null 2>&1 || status=$?
  if ((status != 0)); then pass "$name is not applied yet"; else fail "$name is not applied yet"; fi
  assert_contains "$(cat "$repository_root/docs/RUNBOOK-GERI-ALMA.md")" "patches/$name" "$name is described in the runbook"
done

finish "owner patches"
