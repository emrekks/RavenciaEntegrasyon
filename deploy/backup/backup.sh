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

prune_expired_backups() {
  now="$(date -u +%s)"
  cutoff="$((now - 259200))"
  for expired in /backup/*; do
    test -d "$expired" || continue
    name="${expired##*/}"
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

    if test "$backup_time" -lt "$cutoff"; then
      rm -rf -- "$expired"
      echo "Removed expired backup set: $name"
    fi
  done
}

today="$(date -u +%Y%m%d)"
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
