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

# Retention (newest first):
#   - every set of the last 72 hours (pre-deploy sets stay restorable);
#   - the newest set of each UTC day for BACKUP_KEEP_DAILY days (default 14);
#   - the newest set of each ISO week, older than the daily window, for
#     BACKUP_KEEP_WEEKLY weeks (default 4);
#   - always the newest set, whatever its age.
# Everything else is removed. Names that are not UTC timestamps are never touched.
prune_expired_backups() {
  keep_daily="${BACKUP_KEEP_DAILY:-14}"
  keep_weekly="${BACKUP_KEEP_WEEKLY:-4}"
  now="$(date -u +%s)"
  recent_cutoff="$((now - 259200))"
  daily_cutoff="$((now - keep_daily * 86400))"
  weekly_cutoff="$((now - (keep_daily + keep_weekly * 7 + 7) * 86400))"
  seen_days=" "
  seen_weeks=" "
  weeks_kept=0
  newest_kept=false

  # Timestamp names sort chronologically; walk them newest first.
  # The names contain no whitespace (checked below), so word splitting is safe.
  for path in $(printf '%s\n' /backup/????????T??????Z | sort -r); do
    test -d "$path" || continue
    name="${path##*/}"
    case "$name" in
      ????????T??????Z) ;;
      *) continue ;;
    esac
    case "$name" in
      *[!0-9TZ]*) continue ;;
    esac

    year="$(printf '%s' "$name" | cut -c1-4)"
    month="$(printf '%s' "$name" | cut -c5-6)"
    day="$(printf '%s' "$name" | cut -c7-8)"
    hour="$(printf '%s' "$name" | cut -c10-11)"
    minute="$(printf '%s' "$name" | cut -c12-13)"
    second="$(printf '%s' "$name" | cut -c14-15)"
    backup_time="$(date -u -d "$year-$month-$day $hour:$minute:$second UTC" +%s 2>/dev/null || true)"
    test -n "$backup_time" || continue
    day_key="$year$month$day"
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
