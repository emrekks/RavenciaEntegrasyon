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

# Complete sets carry backup.sh's marker; make_unverified_sets leaves it (and SHA256SUMS) out.
make_sets() { local name; for name in "$@"; do mkdir -p -- "$work/$name"; : > "$work/$name/.verified"; done; }
make_unverified_sets() { local name; for name in "$@"; do mkdir -p -- "$work/$name"; done; }

echo "backup retention"

# --- recent, daily and weekly windows -------------------------------------
kept=(
  20261021T110000Z   # newest
  20261021T080000Z   # within 72 hours
  20261019T200000Z   # within 72 hours
  20261017T203000Z   # daily window: evening set of 17 Oct (after 12:00 UTC)
  20261017T085900Z   # daily window: noon set of 17 Oct (before 12:00 UTC)
  20261015T203000Z   # daily window: evening set of 15 Oct
  20261010T203000Z   # weekly: newest of ISO week 41
  20261001T203000Z   # weekly: week 40
  20260924T203000Z   # weekly: week 39
  20260917T203000Z   # weekly: week 38 (fourth weekly set)
  20260815T000000Z   # monthly: newest of August, older than the weekly window
  20260520T000000Z   # monthly: May
)
removed=(
  20261017T190000Z   # older set of the same half day in the daily window
  20261008T203000Z   # older set of week 41
  20260910T203000Z   # fifth weekly set, still inside the weekly window
  20260801T000000Z   # older set of August
  20250101T000000Z   # older than the monthly window
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

# --- an unverified set holds no slot and goes after a day -----------------
rm -rf -- "${work:?}"/*
make_sets 20261021T110000Z 20261017T080000Z
# Newer than the good set of the same half day, but its dump never finished.
make_unverified_sets 20261017T110000Z 20261021T113000Z
output="$(run_prune)"
assert_dir "$work/20261017T080000Z" "keeps the verified set of a half day whose newest set is unverified"
assert_no_dir "$work/20261017T110000Z" "removes an unverified set older than a day"
assert_contains "$output" "Removed unverified backup set: 20261017T110000Z" "reports removing the unverified set"
assert_dir "$work/20261021T113000Z" "leaves an unverified set younger than a day alone"
assert_dir "$work/20261021T110000Z" "the newest verified set stays the newest kept set"

# --- sets from before the marker count with their SHA256SUMS ---------------
rm -rf -- "${work:?}"/*
make_unverified_sets 20261021T110000Z 20261017T080000Z
: > "$work/20261021T110000Z/SHA256SUMS"
: > "$work/20261017T080000Z/SHA256SUMS"
run_prune >/dev/null
assert_dir "$work/20261017T080000Z" "keeps an older set that has SHA256SUMS but predates the marker"

# --- after the marker, SHA256SUMS alone is not enough ---------------------
rm -rf -- "${work:?}"/*
make_sets 20261119T100000Z
make_unverified_sets 20261117T100000Z
: > "$work/20261117T100000Z/SHA256SUMS"
output="$(BACKUP_NOW_EPOCH="$(date -u -d '2026-11-20 12:00:00 UTC' +%s)" run_prune)"
assert_no_dir "$work/20261117T100000Z" "removes a set written after the marker existed that has no marker"
assert_dir "$work/20261119T100000Z" "keeps the verified set"

# --- working directories --------------------------------------------------
rm -rf -- "${work:?}"/* "$work"/.incomplete-*
make_sets 20261021T110000Z
mkdir -p -- "$work/.incomplete-20261021T113000Z" "$work/.incomplete-20261020T080000Z"
touch -d '2026-10-20 08:00:00 UTC' "$work/.incomplete-20261020T080000Z"
output="$(run_prune)"
assert_eq "" "$output" "retention ignores .incomplete-* working directories"
output="$(
  # shellcheck source=deploy/backup/backup.sh
  source "$repository_root/deploy/backup/backup.sh"
  remove_stale_incomplete_sets
)"
assert_no_dir "$work/.incomplete-20261020T080000Z" "removes a working directory older than 6 hours"
assert_contains "$output" "Removed incomplete backup directory: .incomplete-20261020T080000Z" "reports the stale working directory"
assert_dir "$work/.incomplete-20261021T113000Z" "keeps a working directory still being written"
rm -rf -- "$work"/.incomplete-*

# --- a full run: renamed only when complete, nothing left behind on failure
stubs="$(mktemp -d)"
cat > "$stubs/pg_dump" <<'STUB'
#!/bin/sh
for arg in "$@"; do case "$arg" in --file=*) printf 'dump' > "${arg#--file=}" ;; esac; done
test "${STUB_PG_DUMP_FAIL:-false}" != true
STUB
cat > "$stubs/tar" <<'STUB'
#!/bin/sh
printf 'files' > "$4"
STUB
printf '#!/bin/sh\nexit 0\n' > "$stubs/pg_restore"
chmod +x "$stubs"/*
printf 'secret' > "$stubs/password"
run_backup() {
  (
    unset BACKUP_FUNCTIONS_ONLY BACKUP_NOW_EPOCH
    PATH="$stubs:$PATH" POSTGRES_PASSWORD_FILE="$stubs/password" BACKUP_FORCE=true \
      BACKUP_DISK_LIMIT_PERCENT=100 BACKUP_DISK_HARD_LIMIT_PERCENT=100 sh "$repository_root/deploy/backup/backup.sh"
  )
}
rm -rf -- "${work:?}"/*
status=0
STUB_PG_DUMP_FAIL=true run_backup >/dev/null 2>&1 || status=$?
assert_eq "1" "$((status != 0))" "a failed dump fails the backup"
assert_eq "" "$(find "$work" -mindepth 1 -maxdepth 1 | head -n 1)" "a failed dump leaves neither a set nor a working directory"
output="$(run_backup)"
created="$(find "$work" -mindepth 1 -maxdepth 1 -type d -name '????????T??????Z' -printf '%f\n')"
assert_contains "$output" "Backup set created at $work/$created; verified successfully." "reports the renamed set"
assert_eq "" "$(find "$work" -mindepth 1 -maxdepth 1 -name '.incomplete-*' | head -n 1)" "no working directory remains after success"
for file in .verified SHA256SUMS database.dump private-volumes.tar.gz manifest.json; do
  if [[ -s "$work/$created/$file" ]]; then pass "the set holds $file"; else fail "the set holds $file"; fi
done
if (cd "$work/$created" && sha256sum -c --quiet SHA256SUMS); then pass "the checksums still match after the rename"; else fail "the checksums still match after the rename"; fi
rm -rf -- "$stubs"

# --- the disk guard gives up weekly sets before the monthly ones -----------
rm -rf -- "${work:?}"/*
# Newest, a weekly set (older than the daily window) and an August monthly set (older than the weekly window).
make_sets 20261021T110000Z 20261001T203000Z 20260815T000000Z
output="$(
  # shellcheck source=deploy/backup/backup.sh
  source "$repository_root/deploy/backup/backup.sh"
  # Every set takes 10% of the disk; the limit leaves room for two.
  projected_disk_percent() { find "$work" -mindepth 1 -maxdepth 1 -type d -name '????????T??????Z' | wc -l | awk '{ print $1 * 10 }'; }
  BACKUP_DISK_LIMIT_PERCENT=25 BACKUP_DISK_HARD_LIMIT_PERCENT=100 guard_disk_space
)"
assert_no_dir "$work/20261001T203000Z" "the disk guard removes the weekly set"
assert_dir "$work/20260815T000000Z" "the disk guard keeps the older monthly set while a weekly one can go"
assert_contains "$output" "Disk guard removed weekly backup set 20261001T203000Z" "reports the weekly removal"
output="$(
  # shellcheck source=deploy/backup/backup.sh
  source "$repository_root/deploy/backup/backup.sh"
  projected_disk_percent() { find "$work" -mindepth 1 -maxdepth 1 -type d -name '????????T??????Z' | wc -l | awk '{ print $1 * 10 }'; }
  BACKUP_DISK_LIMIT_PERCENT=15 BACKUP_DISK_HARD_LIMIT_PERCENT=100 guard_disk_space
)"
assert_no_dir "$work/20260815T000000Z" "with no weekly set left the monthly set goes"
assert_contains "$output" "Disk guard removed monthly backup set 20260815T000000Z" "reports the monthly removal"
assert_dir "$work/20261021T110000Z" "the newest set always stays"

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

# --- the off-site copy's marker -------------------------------------------
marked="$work/20261021T120000Z"
mkdir -p -- "$marked"
printf 'dump' > "$marked/database.dump"; printf 'files' > "$marked/private-volumes.tar.gz"; printf '{}' > "$marked/manifest.json"
(cd "$marked" && sha256sum database.dump private-volumes.tar.gz manifest.json > SHA256SUMS)
(
  # shellcheck source=deploy/backup/backup.sh
  source "$repository_root/deploy/backup/backup.sh"
  mark_verified "$marked"
)
assert_eq "$(sha256sum "$marked/SHA256SUMS" | cut -d' ' -f1)" "$(cut -d' ' -f1 "$marked/.verified")" "the marker holds the checksum of the set's SHA256SUMS"
assert_contains "$(cat "$marked/SHA256SUMS")" "manifest.json" "the manifest is covered by the checksums"

finish "backup retention"
