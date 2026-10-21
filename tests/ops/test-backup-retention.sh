#!/usr/bin/env bash
# Retention rules of deploy/backup/backup.sh (prune_expired_backups) against a
# temporary directory and a fixed clock: Wednesday 2026-10-21 12:00 UTC.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

# Read by backup.sh when it is sourced below.
BACKUP_ROOT="$work"
BACKUP_FUNCTIONS_ONLY=true
BACKUP_NOW_EPOCH="$(date -u -d '2026-10-21 12:00:00 UTC' +%s)"
export BACKUP_ROOT BACKUP_FUNCTIONS_ONLY BACKUP_NOW_EPOCH

run_prune() {
  # A subshell: backup.sh uses set -eu and its own globals.
  (
    # shellcheck source=deploy/backup/backup.sh
    source "$repository_root/deploy/backup/backup.sh"
    prune_expired_backups
  )
}

make_sets() { local name; for name in "$@"; do mkdir -p -- "$work/$name"; done; }

echo "backup retention"

# --- recent, daily and weekly windows -------------------------------------
kept=(
  20261021T110000Z   # newest
  20261021T080000Z   # within 72 hours
  20261019T200000Z   # within 72 hours
  20261017T203000Z   # daily window: newest of 17 Oct
  20261015T203000Z   # daily window: newest of 15 Oct
  20261010T203000Z   # weekly: newest of ISO week 41
  20261001T203000Z   # weekly: week 40
  20260924T203000Z   # weekly: week 39
  20260917T203000Z   # weekly: week 38 (fourth weekly set)
)
removed=(
  20261017T100000Z   # second set of a day in the daily window
  20261008T203000Z   # older set of week 41
  20260910T203000Z   # fifth weekly set
  20260801T000000Z   # older than the weekly window
)
make_sets "${kept[@]}" "${removed[@]}"
mkdir -p -- "$work/manual-copy"
output="$(run_prune)"
for name in "${kept[@]}"; do assert_dir "$work/$name" "keeps $name"; done
for name in "${removed[@]}"; do
  assert_no_dir "$work/$name" "removes $name"
  assert_contains "$output" "Removed expired backup set: $name" "reports removing $name"
done
assert_dir "$work/manual-copy" "never touches names that are not UTC timestamps"

# --- the newest set survives whatever its age -----------------------------
rm -rf -- "${work:?}"/*
make_sets 20250101T000000Z 20241201T000000Z
run_prune >/dev/null
assert_dir "$work/20250101T000000Z" "keeps the newest set even when it is a year old"
assert_no_dir "$work/20241201T000000Z" "removes an older set outside every window"

# --- a busy day of deploys keeps only the newest recent sets -------------
rm -rf -- "${work:?}"/*
busy_kept=(20261021T110000Z 20261021T100000Z 20261021T090000Z 20261021T080000Z 20261021T070000Z)
busy_removed=(20261021T060000Z 20261021T050000Z 20261021T040000Z)
make_sets "${busy_kept[@]}" "${busy_removed[@]}" 20261020T200000Z
run_prune >/dev/null
for name in "${busy_kept[@]}"; do assert_dir "$work/$name" "keeps recent set $name"; done
for name in "${busy_removed[@]}"; do assert_no_dir "$work/$name" "removes recent set $name beyond the recent limit"; done
assert_dir "$work/20261020T200000Z" "keeps the newest set of an earlier day through the daily rule"

# --- running twice changes nothing ---------------------------------------
rm -rf -- "${work:?}"/*
make_sets "${kept[@]}"
run_prune >/dev/null
second="$(run_prune)"
assert_eq "" "$second" "a second run removes nothing"

# --- backup_set_epoch accepts only set names ------------------------------
epoch_of() {
  (
    # shellcheck source=deploy/backup/backup.sh
    source "$repository_root/deploy/backup/backup.sh"
    backup_set_epoch "$1"
  )
}
assert_eq "$(date -u -d '2026-10-21 11:00:00 UTC' +%s)" "$(epoch_of 20261021T110000Z)" "parses a set name"
assert_eq "" "$(epoch_of 2026102XT110000Z)" "rejects a name with other characters"
assert_eq "" "$(epoch_of latest)" "rejects a name of another shape"

finish "backup retention"
