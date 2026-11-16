#!/usr/bin/env bash
# Interim point-in-time recovery (VERITABANI-DAGITIM-8, docs/RUNBOOK-GERI-ALMA.md section 9) until the owner picks an
# off-site object store for WAL-G. Works only after docs/patches/VERITABANI-DAGITIM-8-wal-archive.patch is applied:
# PostgreSQL then copies every finished WAL segment, gzip-compressed, into the volume marketplacehub_pg_wal_archive
# (wal/), at the latest every archive_timeout (5 minutes).
#
#   sudo bash deploy/backup/wal-archive.sh basebackup   physical base backup into base/<UTC time>/ (pg_basebackup, tar.gz),
#                                                       keeps the newest KEEP_BASE (2) and the WAL they need
#   sudo bash deploy/backup/wal-archive.sh ship         newest base backup + the WAL after it, age-encrypted, published as
#                                                       offsite-extra/wal/wal-archive.tar.age: the worker's Google Drive
#                                                       copy takes it along (offsite-extra-lib.sh)
#   sudo bash deploy/backup/wal-archive.sh status       archiver counters, base backups and sizes
#
# On the host the recovery point is the last archived segment (at most 5 minutes old): restore-drill.sh --pitr <time>
# recovers into an isolated container. Off the host it is the last Drive copy (twice a day), until WAL goes to an object
# store directly. Timers (installed by the patch): ravencia-wal-basebackup.timer daily, ravencia-wal-ship.timer before
# each Drive copy.
# Test hooks: RAVENCIA_WAL_ARCHIVE_ROOT (a directory instead of the volume) and those of offsite-extra-lib.sh.
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
# shellcheck source=deploy/backup/offsite-extra-lib.sh
source "$script_dir/offsite-extra-lib.sh"

keep_base="${KEEP_BASE:-2}"
container_root=/var/lib/postgresql/wal-archive

archive_root() {
  if [[ -n "${RAVENCIA_WAL_ARCHIVE_ROOT:-}" ]]; then printf '%s' "$RAVENCIA_WAL_ARCHIVE_ROOT"; return; fi
  "${sudo_cmd[@]}" docker volume inspect --format '{{.Mountpoint}}' marketplacehub_pg_wal_archive
}

# Base backups, oldest first (directories named by UTC time; a ".partial" one is still being written).
base_backups() {
  "${sudo_cmd[@]}" find "$1/base" -mindepth 1 -maxdepth 1 -type d -name '????????T??????Z' -printf '%f\n' 2>/dev/null | sort
}

# The first WAL segment a base backup needs, from its backup_label.
start_segment() {
  "${sudo_cmd[@]}" tar -xzOf "$1/base/$2/base.tar.gz" backup_label | sed -n 's/^START WAL LOCATION: .* (file \([0-9A-F]\{24\}\))$/\1/p'
}

# WAL files a recovery from the base backup needs: every segment from its start on, and every timeline history file.
needed_wal() {
  local root="$1" start="$2" name
  while IFS= read -r name; do
    if [[ "$name" == *.history.gz ]] || [[ "${name:0:24}" > "$start" ]] || [[ "${name:0:24}" == "$start" ]]; then
      printf 'wal/%s\n' "$name"
    fi
  done < <("${sudo_cmd[@]}" find "$root/wal" -mindepth 1 -maxdepth 1 -type f -name '*.gz' -printf '%f\n' | sort)
}

case "${1:-}" in
  basebackup)
    postgres="$(extra_service_container postgres)"
    [[ -n "$postgres" ]] || { echo "PostgreSQL is not running." >&2; exit 1; }
    [[ "$("${sudo_cmd[@]}" docker exec "$postgres" psql -X -U marketplacehub -d marketplacehub -Atc 'show archive_mode')" == on ]] \
      || { echo "archive_mode is off: apply docs/patches/VERITABANI-DAGITIM-8-wal-archive.patch first (runbook section 9)." >&2; exit 1; }
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    # As the postgres user, so the files belong to the database server like the WAL it archives. Without -X the backup
    # waits until its last segment is archived; the archive is what makes it consistent.
    "${sudo_cmd[@]}" docker exec -u postgres "$postgres" sh -ceu "
      mkdir -p '$container_root/base'
      rm -rf '$container_root/base/.$stamp.partial'
      pg_basebackup -U marketplacehub -D '$container_root/base/.$stamp.partial' -Ft -z -X none --checkpoint=fast
      mv '$container_root/base/.$stamp.partial' '$container_root/base/$stamp'"
    root="$(archive_root)"
    mapfile -t bases < <(base_backups "$root")
    while (( ${#bases[@]} > keep_base )); do
      "${sudo_cmd[@]}" rm -rf -- "${root:?}/base/${bases[0]}"
      echo "Removed old base backup ${bases[0]}."
      bases=("${bases[@]:1}")
    done
    oldest_start="$(start_segment "$root" "${bases[0]}")"
    [[ -n "$oldest_start" ]] || { echo "No START WAL LOCATION in base backup ${bases[0]}; WAL kept." >&2; exit 1; }
    "${sudo_cmd[@]}" docker exec -u postgres "$postgres" pg_archivecleanup -x .gz "$container_root/wal" "$oldest_start"
    echo "Base backup $stamp done; WAL before $oldest_start (oldest kept base ${bases[0]}) removed."
    ;;
  ship)
    root="$(archive_root)"
    mapfile -t bases < <(base_backups "$root")
    (( ${#bases[@]} )) || { echo "No base backup yet (wal-archive.sh basebackup)." >&2; exit 1; }
    newest="${bases[${#bases[@]}-1]}"
    start="$(start_segment "$root" "$newest")"
    [[ -n "$start" ]] || { echo "No START WAL LOCATION in base backup $newest." >&2; exit 1; }
    recipient="$(extra_recipient "$repository_root/deploy/secrets")"
    [[ -n "$recipient" ]] || { echo "No age public key (panel: Sistem Ayarları › Google Drive Yedeği); nothing shipped." >&2; exit 1; }
    mapfile -t wal < <(needed_wal "$root" "$start")
    staged="$(mktemp -d)"
    trap 'rm -rf -- "$staged"' EXIT
    "${sudo_cmd[@]}" tar -C "$root" -cf - -- "base/$newest" "${wal[@]}" | extra_encrypt "$recipient" > "$staged/wal-archive.tar.age"
    [[ -s "$staged/wal-archive.tar.age" ]] || { echo "Encryption produced nothing." >&2; exit 1; }
    extra_publish "$(extra_backup_root)" wal "$staged"
    echo "WAL archive shipped: base $newest and ${#wal[@]} WAL file(s) from $start, in offsite-extra/wal for the next Drive copy."
    ;;
  status)
    postgres="$(extra_service_container postgres)"
    if [[ -n "$postgres" ]]; then
      "${sudo_cmd[@]}" docker exec "$postgres" psql -X -U marketplacehub -d marketplacehub -c \
        "select current_setting('archive_mode') archive_mode, archived_count, last_archived_wal, last_archived_time, failed_count, last_failed_time from pg_stat_archiver"
    fi
    root="$(archive_root 2>/dev/null || true)"
    if [[ -n "$root" ]]; then
      echo "Base backups: $(base_backups "$root" | tr '\n' ' ')"
      "${sudo_cmd[@]}" du -sh "$root/base" "$root/wal" 2>/dev/null || true
    fi
    ;;
  *)
    echo "usage: wal-archive.sh basebackup | ship | status" >&2
    exit 2
    ;;
esac
