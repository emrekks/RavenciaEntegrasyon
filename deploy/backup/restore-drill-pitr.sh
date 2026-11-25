#!/usr/bin/env bash
# Point-in-time restore drill (VERITABANI-DAGITIM-8, docs/RUNBOOK-GERI-ALMA.md section 9): restores the newest base backup
# older than <time> from the WAL archive volume into an isolated container without network, replays the archived WAL up
# to <time>, promotes, and checks the restored data like restore-drill.sh does. Production is not touched; the archive is
# mounted read-only and archiving is off in the drill.
#
#   sudo bash deploy/backup/restore-drill.sh --pitr '2026-11-20 14:37:00+03'
#
# Needs docs/patches/VERITABANI-DAGITIM-8-wal-archive.patch applied and at least one `wal-archive.sh basebackup`.
# Prints the time recovery stopped at and how long it took (the recovery time to plan with).
set -Eeuo pipefail

if (( EUID == 0 )); then
  sudo() { "$@"; }
fi

target="${1:-}"
[[ -n "$target" ]] || { echo "usage: restore-drill.sh --pitr '<time with offset, e.g. 2026-11-20 14:37:00+03>'" >&2; exit 2; }
target_epoch="$(date -u -d "$target" +%s 2>/dev/null)" || { echo "Not a time: $target" >&2; exit 2; }
(( target_epoch <= $(date -u +%s) )) || { echo "The target time is in the future." >&2; exit 2; }
target_utc="$(date -u -d "@$target_epoch" '+%Y-%m-%d %H:%M:%S+00')"

postgres_image="postgres:18.6@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336"
archive_volume="${RAVENCIA_WAL_ARCHIVE_VOLUME:-marketplacehub_pg_wal_archive}"
archive_root="$(sudo docker volume inspect --format '{{.Mountpoint}}' "$archive_volume")"
# The newest base backup that started before the target.
base=""
while IFS= read -r candidate; do
  [[ "$candidate" =~ ^([0-9]{4})([0-9]{2})([0-9]{2})T([0-9]{2})([0-9]{2})([0-9]{2})Z$ ]] || continue
  started="$(date -u -d "${BASH_REMATCH[1]}-${BASH_REMATCH[2]}-${BASH_REMATCH[3]} ${BASH_REMATCH[4]}:${BASH_REMATCH[5]}:${BASH_REMATCH[6]} UTC" +%s)"
  if (( started < target_epoch )); then base="$candidate"; fi
done < <(sudo find "$archive_root/base" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort)
[[ -n "$base" ]] || { echo "No base backup older than $target_utc in $archive_volume." >&2; exit 1; }

suffix="$(date -u +%Y%m%dt%H%M%S)"
container="marketplacehub_pitr_${suffix}_db"
data_volume="marketplacehub_pitr_${suffix}_data"
pgdata=/var/lib/postgresql/18/docker
started_at="$(date -u +%s)"
cleanup() {
  sudo docker rm -f "$container" >/dev/null 2>&1 || true
  sudo docker volume rm "$data_volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 143' TERM INT

sudo docker volume create "$data_volume" >/dev/null
# The base backup, the recovery target and a restore_command that reads the gzip-compressed archive.
sudo docker run --rm --network none -v "$data_volume:/var/lib/postgresql" -v "$archive_volume:/wal-archive:ro" \
  "$postgres_image" sh -ceu "
    mkdir -p '$pgdata'
    tar -xzf '/wal-archive/base/$base/base.tar.gz' -C '$pgdata'
    cat >> '$pgdata/postgresql.auto.conf' <<'CONF'
restore_command = 'gunzip -c /wal-archive/wal/%f.gz > %p'
recovery_target_time = '$target_utc'
recovery_target_action = 'promote'
CONF
    touch '$pgdata/recovery.signal'
    chown -R postgres:postgres /var/lib/postgresql
    chmod 0700 '$pgdata'"
echo "PITR drill: base backup $base restored; replaying WAL to $target_utc."

sudo docker run -d --name "$container" --network none -v "$data_volume:/var/lib/postgresql" -v "$archive_volume:/wal-archive:ro" \
  "$postgres_image" postgres -c archive_mode=off -c max_connections=20 >/dev/null
for _ in $(seq 1 1800); do
  if [[ "$(sudo docker exec "$container" psql -X -U marketplacehub -d marketplacehub -Atc 'select pg_is_in_recovery()' 2>/dev/null || true)" == f ]]; then break; fi
  if [[ "$(sudo docker inspect -f '{{.State.Running}}' "$container")" != true ]]; then
    sudo docker logs --tail 40 "$container" >&2
    echo "PITR drill: PostgreSQL stopped during recovery." >&2
    exit 1
  fi
  sleep 1
done
[[ "$(sudo docker exec "$container" psql -X -U marketplacehub -d marketplacehub -Atc 'select pg_is_in_recovery()')" == f ]] \
  || { echo "PITR drill: recovery did not finish within 30 minutes." >&2; exit 1; }

tenants="$(sudo docker exec "$container" psql -X -U marketplacehub -d marketplacehub -Atc 'select count(*) from iam.tenants')"
migrations="$(sudo docker exec "$container" psql -X -U marketplacehub -d marketplacehub -Atc 'select count(*) from public."__EFMigrationsHistory"')"
[[ "$tenants" =~ ^[1-9][0-9]*$ && "$migrations" =~ ^[1-9][0-9]*$ ]] || { echo "PITR drill: restored database is empty." >&2; exit 1; }
stopped="$(sudo docker logs "$container" 2>&1 | grep -oE 'recovery stopping before [^,]*, time [0-9: +-]+' | tail -n 1 || true)"
# The last transaction replayed (it stays readable after the promotion): never after the target, and how far before it
# recovery stopped is the data a restore to that time would lose.
replayed_epoch="$(sudo docker exec "$container" psql -X -U marketplacehub -d marketplacehub -Atc \
  "select coalesce(extract(epoch from pg_last_xact_replay_timestamp())::bigint::text, '')")"
if [[ "$replayed_epoch" =~ ^[0-9]+$ ]]; then
  (( replayed_epoch <= target_epoch )) || { echo "PITR drill: recovery replayed a transaction after the target ($(date -u -d "@$replayed_epoch" '+%Y-%m-%d %H:%M:%S+00'))." >&2; exit 1; }
  replayed="lastReplayed=$(date -u -d "@$replayed_epoch" '+%Y-%m-%dT%H:%M:%SZ') gapToTargetSeconds=$(( target_epoch - replayed_epoch ))"
else
  replayed="lastReplayed=(no transaction replayed after the base backup)"
fi
printf 'PITR drill passed: base=%s target=%s %s %s tenants=%s migrations=%s durationSeconds=%s\n' \
  "$base" "$target_utc" "${stopped:-(target after the last archived transaction)}" "$replayed" "$tenants" "$migrations" "$(( $(date -u +%s) - started_at ))"
