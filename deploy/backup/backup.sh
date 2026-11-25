#!/bin/sh
set -eu
umask 077

# The backup container mounts the staging volume at /backup. BACKUP_ROOT and
# BACKUP_NOW_EPOCH exist for tests/ops (retention tests against a temporary
# directory and a fixed clock); BACKUP_FUNCTIONS_ONLY=true, when the file is
# sourced, defines the functions and returns before any backup work.
backup_root="${BACKUP_ROOT:-/backup}"

verify_backup() {
  backup_dir="$1"
  test -s "$backup_dir/manifest.json"
  test -s "$backup_dir/database.dump"
  test -s "$backup_dir/private-volumes.tar.gz"
  (cd "$backup_dir" && sha256sum -c SHA256SUMS >/dev/null && pg_restore --list database.dump >/dev/null)
}

# Retention (a set is ~0.3-0.5 GB on 12 Nov 2026; the Hostinger root disk is 193 GB):
#   - the newest BACKUP_KEEP_RECENT sets (default 5) of the last 72 hours, so the
#     latest pre-deploy sets stay restorable (every deploy writes one; a busy day
#     of deploys must not fill the disk);
#   - for BACKUP_KEEP_DAILY days (default 7) the newest set of each half day: the
#     timer writes two sets a day, 11:59 and 23:59 Istanbul (08:59 / 20:59 UTC),
#     one before and one after 12:00 UTC;
#   - the newest set of each ISO week, older than the daily window, for
#     BACKUP_KEEP_WEEKLY weeks (default 4);
#   - the newest set of each calendar month, older than the weekly window, for
#     BACKUP_KEEP_MONTHLY months (default 6);
#   - always the newest set, whatever its age.
# Everything else is removed. Names that are not UTC timestamps are never touched.
# Only complete sets count (backup_set_complete): a set is written under a
# ".incomplete-<stamp>" name and renamed to its timestamp only after its own check
# passed, so retention, the disk guard, healthwatch and the restore drill never take
# a half-written set for the newest one. A timestamp-named set without the marker
# (left by an older run) holds no retention slot and is removed after a day;
# ".incomplete-*" leftovers of a run that died are removed after 6 hours.
# Disk guard (before a new set is written): if the filesystem holding /backup
# would pass BACKUP_DISK_LIMIT_PERCENT (default 85) once a set the size of the
# newest one is added, the oldest weekly sets are removed first; if it is still
# above, a "DISK_GUARD_WARNING:" line is printed (the host-side caller turns it
# into an operator alert). Above BACKUP_DISK_HARD_LIMIT_PERCENT (default 95) the
# backup is refused, so a dump can never fill the disk under PostgreSQL.

retention_cutoffs() {
  keep_daily="${BACKUP_KEEP_DAILY:-7}"
  keep_weekly="${BACKUP_KEEP_WEEKLY:-4}"
  keep_monthly="${BACKUP_KEEP_MONTHLY:-6}"
  keep_recent="${BACKUP_KEEP_RECENT:-5}"
  now="${BACKUP_NOW_EPOCH:-$(date -u +%s)}"
  recent_cutoff="$((now - 259200))"
  daily_cutoff="$((now - keep_daily * 86400))"
  weekly_cutoff="$((now - (keep_daily + keep_weekly * 7 + 7) * 86400))"
  monthly_cutoff="$((weekly_cutoff - keep_monthly * 31 * 86400))"
}

# Prints the epoch of a YYYYMMDDTHHMMSSZ set name; prints nothing for other names.
backup_set_epoch() {
  case "$1" in
    ????????T??????Z) ;;
    *) return 0 ;;
  esac
  case "$1" in
    *[!0-9TZ]*) return 0 ;;
  esac
  date -u -d "$(printf '%s' "$1" | cut -c1-4)-$(printf '%s' "$1" | cut -c5-6)-$(printf '%s' "$1" | cut -c7-8) $(printf '%s' "$1" | cut -c10-11):$(printf '%s' "$1" | cut -c12-13):$(printf '%s' "$1" | cut -c14-15) UTC" +%s 2>/dev/null || true
}

# Sets named before this (written before backup.sh marked verified sets, 13 Nov 2026, and before the rename
# above) count as complete when their SHA256SUMS exists: the old script wrote it only after the dump and the
# file archive had finished.
legacy_set_cutoff=20261116000000

# True for a set directory whose own check passed (.verified), or an older set with its SHA256SUMS.
backup_set_complete() {
  test -f "$1/.verified" && return 0
  test -f "$1/SHA256SUMS" || return 1
  complete_digits="$(printf '%s' "${1##*/}" | tr -d 'TZ')"
  case "$complete_digits" in
    ''|*[!0-9]*) return 1 ;;
  esac
  test "$complete_digits" -lt "$legacy_set_cutoff"
}

# Removes the working directories of runs that died before their set was complete.
remove_stale_incomplete_sets() {
  find "$backup_root" -mindepth 1 -maxdepth 1 -type d -name '.incomplete-*' -mmin +360 -print 2>/dev/null \
    | while IFS= read -r stale; do
        rm -rf -- "$stale"
        echo "Removed incomplete backup directory: ${stale##*/}"
      done
}

# Lists the set directories, oldest first ("-r": newest first).
# The names contain no whitespace (checked by backup_set_epoch), so word splitting is safe.
list_backup_sets() {
  printf '%s\n' "$backup_root"/????????T??????Z | sort "$@"
}

prune_expired_backups() {
  retention_cutoffs
  seen_half_days=" "
  seen_weeks=" "
  seen_months=" "
  weeks_kept=0
  months_kept=0
  recent_kept=0
  newest_kept=false

  for path in $(list_backup_sets -r); do
    test -d "$path" || continue
    name="${path##*/}"
    backup_time="$(backup_set_epoch "$name")"
    test -n "$backup_time" || continue
    if ! backup_set_complete "$path"; then
      # Never a slot holder; a day of grace in case a run of an older script is still writing it.
      if test "$backup_time" -lt "$((now - 86400))"; then
        rm -rf -- "${backup_root:?}/$name"
        echo "Removed unverified backup set: $name"
      fi
      continue
    fi
    # 20261021AM / 20261021PM: before or after 12:00 UTC.
    if test "$(printf '%s' "$name" | cut -c10-11)" -lt 12; then half_day_key="$(printf '%s' "$name" | cut -c1-8)AM"; else half_day_key="$(printf '%s' "$name" | cut -c1-8)PM"; fi
    week_key="$(date -u -d "@$backup_time" +%G%V)"
    month_key="$(printf '%s' "$name" | cut -c1-6)"

    keep=false
    if test "$newest_kept" = false; then
      keep=true
      test "$backup_time" -lt "$recent_cutoff" || recent_kept="$((recent_kept + 1))"
    elif test "$backup_time" -ge "$recent_cutoff" && test "$recent_kept" -lt "$keep_recent"; then
      keep=true
      recent_kept="$((recent_kept + 1))"
    elif test "$backup_time" -ge "$daily_cutoff"; then
      case "$seen_half_days" in
        *" $half_day_key "*) ;;
        *) keep=true ;;
      esac
    elif test "$backup_time" -ge "$weekly_cutoff" && test "$weeks_kept" -lt "$keep_weekly"; then
      case "$seen_weeks" in
        *" $week_key "*) ;;
        *) keep=true; weeks_kept="$((weeks_kept + 1))" ;;
      esac
    elif test "$backup_time" -lt "$weekly_cutoff" && test "$backup_time" -ge "$monthly_cutoff" && test "$months_kept" -lt "$keep_monthly"; then
      case "$seen_months" in
        *" $month_key "*) ;;
        *) keep=true; months_kept="$((months_kept + 1))" ;;
      esac
    fi

    if test "$keep" = true; then
      newest_kept=true
      seen_half_days="$seen_half_days$half_day_key "
      seen_weeks="$seen_weeks$week_key "
      seen_months="$seen_months$month_key "
    else
      rm -rf -- "${backup_root:?}/$name"
      echo "Removed expired backup set: $name"
    fi
  done
}

# Projected use (%) of the filesystem holding /backup after adding $1 KiB,
# computed like df's Use% (used / (used + available), reserved blocks excluded).
projected_disk_percent() {
  df -Pk "$backup_root" | awk -v extra="$1" 'NR == 2 { total = $3 + $4; if (total <= 0) { print 0 } else { printf "%d\n", (($3 + extra) * 100 + total - 1) / total } }'
}

guard_disk_space() {
  limit="${BACKUP_DISK_LIMIT_PERCENT:-85}"
  hard_limit="${BACKUP_DISK_HARD_LIMIT_PERCENT:-95}"
  retention_cutoffs
  newest=""
  for path in $(list_backup_sets -r); do
    if test -d "$path" && test -n "$(backup_set_epoch "${path##*/}")" && backup_set_complete "$path"; then newest="$path"; break; fi
  done
  estimate_kib=0
  if test -n "$newest"; then estimate_kib="$(du -sk "$newest" | cut -f1)"; fi
  projected="$(projected_disk_percent "$estimate_kib")"

  # Only sets older than the daily window and the last 72 hours are given up for
  # space; daily and recent sets stay. Weekly sets go first (oldest first), the
  # monthly ones (older than the weekly window) only when that was not enough:
  # they are the only copies of their months.
  for guard_pass in weekly monthly; do
    test "$projected" -gt "$limit" || break
    for path in $(list_backup_sets); do
      test -d "$path" || continue
      test "$path" != "$newest" || continue
      backup_time="$(backup_set_epoch "${path##*/}")"
      test -n "$backup_time" || continue
      if test "$backup_time" -ge "$daily_cutoff" || test "$backup_time" -ge "$recent_cutoff"; then continue; fi
      if test "$guard_pass" = weekly && test "$backup_time" -lt "$weekly_cutoff"; then continue; fi
      rm -rf -- "$path"
      projected="$(projected_disk_percent "$estimate_kib")"
      echo "Disk guard removed $guard_pass backup set ${path##*/}; projected disk use now ${projected}%."
      test "$projected" -gt "$limit" || break
    done
  done

  if test "$projected" -gt "$limit"; then
    echo "DISK_GUARD_WARNING: backup disk would reach ${projected}% (limit ${limit}%) with a ~$((estimate_kib / 1024)) MiB set after removing every weekly and monthly set; free space or lower retention."
  fi
  if test "$projected" -gt "$hard_limit"; then
    echo "Backup refused: the disk would reach ${projected}% (hard limit ${hard_limit}%)." >&2
    exit 1
  fi
}

# The marker the off-site copy looks for (BackupSetDirectory.VerifiedMarker): written only after verify_backup passed,
# with the set's own check-sum line in it.
mark_verified() {
  (cd "$1" && sha256sum SHA256SUMS > .verified)
}

# The worker's off-site copy (Sistem Ayarları › Google Drive Yedeği) reads the set as the app's group
# (BACKUP_READER_GID, uid/gid 1654 in the app image) through a read-only mount: the volume root and the new set
# become that group's, read-only for it. The worker already holds the database credentials and the private volumes,
# so this exposes nothing new to it. A failure here never fails the backup; that set is only not copied off-site.
share_with_reader() {
  reader_gid="${BACKUP_READER_GID:-}"
  test -n "$reader_gid" || return 0
  case "$reader_gid" in *[!0-9]*) echo "BACKUP_READER_GID must be numeric; set not shared."; return 0 ;; esac
  # "$1"/* leaves the dot-named marker out; it is named on its own.
  if chgrp "$reader_gid" "$backup_root" "$1" "$1"/* "$1/.verified" && chmod 0750 "$backup_root" "$1" && chmod 0640 "$1"/* "$1/.verified"; then
    echo "Backup set $1 readable by group $reader_gid (off-site copy)."
  else
    echo "Could not hand backup set $1 to group $reader_gid; the off-site copy will skip it."
  fi
}

if test "${BACKUP_FUNCTIONS_ONLY:-false}" = "true"; then
  # Only reached when sourced by the tests; "return" ends the sourcing.
  return 0
fi

test -r "${POSTGRES_PASSWORD_FILE:?missing POSTGRES_PASSWORD_FILE}"
PGPASSWORD="$(cat "$POSTGRES_PASSWORD_FILE")"
export PGPASSWORD

today="$(date -u +%Y%m%d)"
# Deploys, rollbacks and the nightly timer pass BACKUP_FORCE=true; only an ad-hoc
# run may reuse today's verified set.
if test "${BACKUP_FORCE:-false}" != "true"; then
  today_backup=""
  for candidate in "$backup_root/$today"T??????Z; do
    test -d "$candidate" || continue
    if verify_backup "$candidate"; then today_backup="$candidate"; fi
  done

  if test -n "$today_backup"; then
    prune_expired_backups
    echo "Backup set created at $today_backup; today's verified backup reused."
    exit 0
  fi
fi

# Apply retention before writing, then make sure the new set fits.
remove_stale_incomplete_sets
prune_expired_backups
guard_disk_space

# The set is written under a name retention and every reader skip, and gets its timestamp name only once complete
# and verified; a failure on the way removes the working directory (trap) instead of leaving a half set behind.
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="$backup_root/$stamp"
staging="$backup_root/.incomplete-$stamp"
while test -e "$target" || ! mkdir "$staging" 2>/dev/null; do
  sleep 1
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  target="$backup_root/$stamp"
  staging="$backup_root/.incomplete-$stamp"
done
trap 'rm -rf -- "$staging"' EXIT
pg_dump --format=custom --file="$staging/database.dump"
tar -C /source -czf "$staging/private-volumes.tar.gz" files dp-keys
printf '{"createdAt":"%s","postgresMajor":18,"filesIncluded":true,"dataProtectionKeysIncluded":true}\n' "$stamp" > "$staging/manifest.json"
# The manifest is covered by the checksums too, so nothing in the set travels unchecked. Names are relative: the
# directory is renamed after this.
(cd "$staging" && sha256sum database.dump private-volumes.tar.gz manifest.json > SHA256SUMS)
verify_backup "$staging"
# Only a set whose own check passed carries the marker; the off-site copy (BackupSetDirectory) sends no other set.
mark_verified "$staging"
mv -T -- "$staging" "$target"
share_with_reader "$target"
prune_expired_backups
echo "Backup set created at $target; verified successfully."
