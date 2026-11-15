#!/usr/bin/env bash
# The PostgreSQL image is pinned in several places that Dependabot does not update together (it only edits
# deploy/compose): production, the backup job and the test lane must all run the same release (VERITABANI-DAGITIM-14).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

echo "postgres digest sync"

files=(deploy/compose/compose.yaml deploy/backup/restore-drill.sh .github/workflows/ci.yml)
for file in "${files[@]}"; do
  count="$(grep -cE 'postgres:[0-9.]+@sha256:[0-9a-f]{64}' "$repository_root/$file" || true)"
  if ((count > 0)); then pass "$file pins postgres by digest"; else fail "$file pins postgres by digest"; fi
done

pins="$(cd -- "$repository_root" && grep -ohE 'postgres:[0-9.]+@sha256:[0-9a-f]{64}' "${files[@]}" | sort -u)"
assert_eq "1" "$(printf '%s\n' "$pins" | grep -c .)" "one postgres image across ${files[*]} (found: ${pins//$'\n'/ })"

# An unpinned postgres image would slip past the check above.
unpinned="$(cd -- "$repository_root" && grep -nE 'postgres:[0-9][^@[:space:]"]*([[:space:]"]|$)' "${files[@]}" || true)"
assert_eq "" "$unpinned" "no postgres image without a digest"

finish "postgres digest sync"
