#!/usr/bin/env bash
set -Eeuo pipefail

# Five-minute health watch (ravencia-healthwatch.timer), around the clock:
#   - GET /health/ready and /health/queues through the edge, entered via loopback
#     (the edge answers them only to private source addresses);
#   - the Worker container's heartbeat health;
#   - root disk use (RAVENCIA_DISK_ALERT_PERCENT, default 85), PostgreSQL client
#     connections against max_connections (RAVENCIA_DB_CONNECTION_ALERT_PERCENT,
#     default 80) and the newest backup set's age (RAVENCIA_BACKUP_MAX_AGE_HOURS,
#     default 30);
#   - self-heal: an API container that Docker reports "unhealthy" on two
#     consecutive runs is restarted (at most every 30 minutes, and only while
#     PostgreSQL itself is healthy, since restarting cannot fix a database outage).
# A problem is alerted (send-alert.sh) after two consecutive failing runs, then
# hourly while it lasts, and once more when it clears.

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
# Read by lib.sh (read_env_or):
# shellcheck disable=SC2034
environment_file="$repository_root/deploy/secrets/production.env"
state_dir="${STATE_DIRECTORY:-${RAVENCIA_HEALTHWATCH_STATE:-$HOME/.local/state/ravencia-healthwatch}}"
mkdir -p -- "$state_dir"

sudo_cmd=(sudo -n)
# shellcheck source=/dev/null
source "$script_dir/lib.sh"

now="$(date -u +%s)"
uptime_seconds="$(cut -d. -f1 /proc/uptime)"
if (( uptime_seconds < 600 )); then
  echo "Host booted ${uptime_seconds}s ago; the stack is still starting."
  exit 0
fi

read_state() {
  if [[ -s "$state_dir/$1" ]]; then cat -- "$state_dir/$1"; else printf '%s' "${2:-}"; fi
}
write_state() { printf '%s' "$2" > "$state_dir/$1"; }
alert() { "$script_dir/send-alert.sh" "$1" || echo "Alert delivery failed: $1" >&2; }

service_container() {
  "${sudo_cmd[@]}" docker ps -aq \
    --filter label=com.docker.compose.project=marketplacehub \
    --filter "label=com.docker.compose.service=$1" | head -n 1
}
container_health() {
  local id
  id="$(service_container "$1")"
  [[ -n "$id" ]] || { printf 'missing'; return; }
  "${sudo_cmd[@]}" docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id"
}

site_address="$(read_env_or MARKETPLACEHUB_SITE_ADDRESS https://panel.ravencia.com 2>/dev/null || echo https://panel.ravencia.com)"
mapfile -t resolve < <(loopback_resolve_args "$site_address")
probe() {
  local path="$1" status
  status="$(curl "${resolve[@]}" --connect-timeout 3 --max-time 10 --silent --output /dev/null --write-out '%{http_code}' "$site_address$path" || true)"
  [[ "$status" == "200" ]] || problems+=("$path HTTP ${status:-000}")
}

problems=()
probe /health/ready
probe /health/queues
worker_health="$(container_health worker)"
[[ "$worker_health" == "healthy" ]] || problems+=("worker $worker_health")
api_health="$(container_health api)"
[[ "$api_health" == "healthy" ]] || problems+=("api $api_health")

# Capacity checks: they alert before the disk, PostgreSQL's connection slots or
# the backup schedule actually break something.
disk_limit="${RAVENCIA_DISK_ALERT_PERCENT:-85}"
disk_percent="$(df --output=pcent / | tail -n 1 | tr -dc '0-9')"
if [[ -n "$disk_percent" ]] && (( disk_percent >= disk_limit )); then
  problems+=("root disk ${disk_percent}% used (limit ${disk_limit}%)")
fi

connection_limit_percent="${RAVENCIA_DB_CONNECTION_ALERT_PERCENT:-80}"
postgres_id="$(service_container postgres)"
if [[ -n "$postgres_id" ]]; then
  # "<client connections> <max_connections>"; empty when PostgreSQL does not answer
  # (that outage already shows up as /health/ready failing).
  connection_usage="$("${sudo_cmd[@]}" docker exec "$postgres_id" psql -U marketplacehub -d marketplacehub -AtF ' ' -c \
    "select count(*) filter (where backend_type = 'client backend'), current_setting('max_connections') from pg_stat_activity" 2>/dev/null || true)"
  if [[ "$connection_usage" =~ ^([0-9]+)\ ([0-9]+)$ ]]; then
    connections="${BASH_REMATCH[1]}"
    max_connections="${BASH_REMATCH[2]}"
    if (( max_connections > 0 && connections * 100 >= max_connections * connection_limit_percent )); then
      problems+=("PostgreSQL connections ${connections}/${max_connections} (limit ${connection_limit_percent}%)")
    fi
  fi
fi

# Newest backup set in the staging volume (11:59 and 23:59 Istanbul and before
# every deploy). 30 hours leaves room for one missed run; after a stop the backup
# timer's Persistent=true catch-up runs within the first minutes after boot.
backup_max_age_hours="${RAVENCIA_BACKUP_MAX_AGE_HOURS:-30}"
backup_mountpoint="$("${sudo_cmd[@]}" docker volume inspect --format '{{.Mountpoint}}' marketplacehub_backup_staging 2>/dev/null || true)"
if [[ -n "$backup_mountpoint" ]]; then
  newest_backup="$("${sudo_cmd[@]}" find "$backup_mountpoint" -mindepth 1 -maxdepth 1 -type d -name '????????T??????Z' -printf '%f\n' 2>/dev/null | sort | tail -n 1 || true)"
  if [[ "$newest_backup" =~ ^([0-9]{4})([0-9]{2})([0-9]{2})T([0-9]{2})([0-9]{2})([0-9]{2})Z$ ]]; then
    backup_epoch="$(date -u -d "${BASH_REMATCH[1]}-${BASH_REMATCH[2]}-${BASH_REMATCH[3]} ${BASH_REMATCH[4]}:${BASH_REMATCH[5]}:${BASH_REMATCH[6]} UTC" +%s 2>/dev/null)" || backup_epoch="$now"
    backup_age_hours="$(( (now - backup_epoch) / 3600 ))"
    if (( backup_age_hours >= backup_max_age_hours )); then
      problems+=("newest backup $newest_backup is ${backup_age_hours}h old (limit ${backup_max_age_hours}h)")
    fi
  else
    problems+=("no backup set found in marketplacehub_backup_staging")
  fi
fi

# Self-heal the API (Docker's restart policy does not act on "unhealthy").
if [[ "$api_health" == "unhealthy" ]]; then
  api_unhealthy_runs="$(( $(read_state api_unhealthy_runs 0) + 1 ))"
  write_state api_unhealthy_runs "$api_unhealthy_runs"
  last_restart="$(read_state api_last_restart 0)"
  postgres_health="$(container_health postgres)"
  if (( api_unhealthy_runs >= 2 && now - last_restart >= 1800 )); then
    if [[ "$postgres_health" == "healthy" ]]; then
      echo "Restarting the unhealthy API container."
      "${sudo_cmd[@]}" docker restart --time 30 "$(service_container api)" >/dev/null
      write_state api_last_restart "$now"
      write_state api_unhealthy_runs 0
      alert "API was unhealthy for two checks and has been restarted automatically; check: journalctl CONTAINER_NAME=marketplacehub-api-1 --since -30min"
    else
      echo "API is unhealthy but PostgreSQL is $postgres_health; not restarting the API."
    fi
  fi
else
  write_state api_unhealthy_runs 0
fi

failing_runs="$(read_state failing_runs 0)"
last_alert="$(read_state last_alert 0)"
if ((${#problems[@]})); then
  summary="$(printf '%s; ' "${problems[@]}")"
  summary="${summary%; }"
  failing_runs=$((failing_runs + 1))
  write_state failing_runs "$failing_runs"
  echo "Unhealthy (run $failing_runs): $summary"
  if (( failing_runs >= 2 && now - last_alert >= 3600 )); then
    alert "Health check failing for ~$(( failing_runs * 5 )) min: $summary"
    write_state last_alert "$now"
    write_state alerted true
  fi
  exit 1
fi

if [[ "$(read_state alerted false)" == true ]]; then
  alert "Recovered: /health/ready, /health/queues, API, Worker, disk, connections and backup age are fine again."
fi
write_state failing_runs 0
write_state last_alert 0
write_state alerted false
echo "Healthy."
