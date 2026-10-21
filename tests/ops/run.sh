#!/usr/bin/env bash
# Runs the operations script tests (plain bash, no test framework needed):
#   bash tests/ops/run.sh
# Each tests/ops/test-*.sh is a standalone script that exits non-zero on failure.
# Requires GNU coreutils (date -d, tail --pid), git and flock, as on the server.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
failed=()
for test_file in "$tests_dir"/test-*.sh; do
  if ! bash "$test_file"; then
    failed+=("${test_file##*/}")
  fi
done

if ((${#failed[@]})); then
  printf 'Failed: %s\n' "${failed[*]}" >&2
  exit 1
fi
echo "All operations script tests passed."
