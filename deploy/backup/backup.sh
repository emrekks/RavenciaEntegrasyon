#!/bin/sh
set -eu
umask 077
test -r "${POSTGRES_PASSWORD_FILE:?missing POSTGRES_PASSWORD_FILE}"
PGPASSWORD="$(cat "$POSTGRES_PASSWORD_FILE")"
export PGPASSWORD

verify_backup() {
  backup_dir="$1"
  test -s "$backup_dir/manifest.json"
  test -s "$backup_dir/database.dump"
  test -s "$backup_dir/private-volumes.tar.gz"
  (cd "$backup_dir" && sha256sum -c SHA256SUMS >/dev/null && pg_restore --list database.dump >/dev/null)
}

# Retention (a set is ~1.1 GB; sized for the 72 GB root disk):
#   - every set of the last 72 hours (pre-deploy sets stay restorable);
#   - the newest set of each UTC day for BACKUP_KEEP_DAILY days (default 7);
#   - the newest set of each ISO week, older than the daily window, for
#     BACKUP_KEEP_WEEKLY weeks (default 4);
#   - always the newest set, whatever its age.
# Everything else is removed. Names that are not UTC timestamps are never touched.
# Disk guard (before a new set is written): if the filesystem holding /backup
# would pass BACKUP_DISK_LIMIT_PERCENT (default 85) once a set the size of the
# newest one is added, the oldest weekly sets are removed first; if it is still
# above, a "DISK_GUARD_WARNING:" line is printed (the host-side caller turns it
# into an operator alert). Above BACKUP_DISK_HARD_LIMIT_PERCENT (default 95) the
# backup is refused, so a dump can never fill the disk under PostgreSQL.

retention_cutoffs() {
  keep_daily="${BACKUP_KEEP_DAILY:-7}"
  keep_weekly="${BACKUP_KEEP_WEEKLY:-4}"
  now="$(date -u +%s)"
  recent_cutoff="$((now - 259200))"
  daily_cutoff="$((now - keep_daily * 86400))"
  weekly_cutoff="$((now - (keep_daily + keep_weekly * 7 + 7) * 86400))"
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

# Lists the set directories, oldest first ("-r": newest first).
# The names contain no whitespace (checked by backup_set_epoch), so word splitting is safe.
list_backup_sets() {
  printf '%s\n' /backup/????????T??????Z | sort "$@"
}

prune_expired_backups() {
  retention_cutoffs
  seen_days=" "
  seen_weeks=" "
  weeks_kept=0
  newest_kept=false

  for path in $(list_backup_sets -r); do
    test -d "$path" || continue
    name="${path##*/}"
    backup_time="$(backup_set_epoch "$name")"
    test -n "$backup_time" || continue
    day_key="$(printf '%s' "$name" | cut -c1-8)"
    week_key="$(date -u -d "@$backup_time" +%G%V)"

    keep=false
    if test "$newest_kept" = false; then
      keep=true
    elif test "$backup_time" -ge "$recent_cutoff"; then
      keep=true
    elif test "$backup_time" -ge "$daily_cutoff"; then
      case "$seen_days" in
        *" $day_key "*) ;;
        *) keep=true ;;
      esac
    elif test "$backup_time" -ge "$weekly_cutoff" && test "$weeks_kept" -lt "$keep_weekly"; then
      case "$seen_weeks" in
        *" $week_key "*) ;;
        *) keep=true; weeks_kept="$((weeks_kept + 1))" ;;
      esac
    fi

    if test "$keep" = true; then
      newest_kept=true
      seen_days="$seen_days$day_key "
      seen_weeks="$seen_weeks$week_key "
    else
      rm -rf -- "/backup/$name"
      echo "Removed expired backup set: $name"
    fi
  done
}

# Projected use (%) of the filesystem holding /backup after adding $1 KiB,
# computed like df's Use% (used / (used + available), reserved blocks excluded).
projected_disk_percent() {
  df -Pk /backup | awk -v extra="$1" 'NR == 2 { total = $3 + $4; if (total <= 0) { print 0 } else { printf "%d\n", (($3 + extra) * 100 + total - 1) / total } }'
}

guard_disk_space() {
  limit="${BACKUP_DISK_LIMIT_PERCENT:-85}"
  hard_limit="${BACKUP_DISK_HARD_LIMIT_PERCENT:-95}"
  retention_cutoffs
  newest=""
  for path in $(list_backup_sets -r); do
    if test -d "$path" && test -n "$(backup_set_epoch "${path##*/}")"; then newest="$path"; break; fi
  done
  estimate_kib=0
  if test -n "$newest"; then estimate_kib="$(du -sk "$newest" | cut -f1)"; fi
  projected="$(projected_disk_percent "$estimate_kib")"

  if test "$projected" -gt "$limit"; then
    # Oldest first: only weekly sets (older than the daily window and the last
    # 72 hours) are given up for space; daily and recent sets stay.
    for path in $(list_backup_sets); do
      test -d "$path" || continue
      test "$path" != "$newest" || continue
      backup_time="$(backup_set_epoch "${path##*/}")"
      test -n "$backup_time" || continue
      if test "$backup_time" -lt "$daily_cutoff" && test "$backup_time" -lt "$recent_cutoff"; then
        rm -rf -- "$path"
        projected="$(projected_disk_percent "$estimate_kib")"
        echo "Disk guard removed weekly backup set ${path##*/}; projected disk use now ${projected}%."
        test "$projected" -gt "$limit" || break
      fi
    done
  fi

  if test "$projected" -gt "$limit"; then
    echo "DISK_GUARD_WARNING: backup disk would reach ${projected}% (limit ${limit}%) with a ~$((estimate_kib / 1024)) MiB set after removing every weekly set; free space or lower retention."
  fi
  if test "$projected" -gt "$hard_limit"; then
    echo "Backup refused: the disk would reach ${projected}% (hard limit ${hard_limit}%)." >&2
    exit 1
  fi
}

today="$(date -u +%Y%m%d)"
# Deploys, rollbacks and the nightly timer pass BACKUP_FORCE=true; only an ad-hoc
# run may reuse today's verified set.
if test "${BACKUP_FORCE:-false}" != "true"; then
  today_backup=""
  for candidate in /backup/"$today"T??????Z; do
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
prune_expired_backups
guard_disk_space

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="/backup/$stamp"
while ! mkdir "$target" 2>/dev/null; do
  sleep 1
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  target="/backup/$stamp"
done
pg_dump --format=custom --file="$target/database.dump"
tar -C /source -czf "$target/private-volumes.tar.gz" files dp-keys
sha256sum "$target/database.dump" "$target/private-volumes.tar.gz" > "$target/SHA256SUMS"
printf '{"createdAt":"%s","postgresMajor":18,"filesIncluded":true,"dataProtectionKeysIncluded":true}\n' "$stamp" > "$target/manifest.json"
verify_backup "$target"
prune_expired_backups
echo "Backup set created at $target; verified successfully."
