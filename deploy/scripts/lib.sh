#!/usr/bin/env bash
# Shared helpers for the deployment scripts. Source this file; do not execute it.
#
# The caller defines, before sourcing:
#   repository_root    absolute path of the checked-out repository
#   environment_file   deploy/secrets/production.env
#   compose            array: the docker compose command prefix (env, --env-file, -f ...)
# and optionally:
#   sudo_cmd           array: how to escalate (default: sudo -n; the interactive first
#                      installation uses plain sudo so it may prompt once)
#
# The variables above are assigned by the sourcing script:
# shellcheck disable=SC2154

if ! declare -p sudo_cmd >/dev/null 2>&1; then
  sudo_cmd=(sudo -n)
fi

# Every production script needs these files; callers may pass extra names.
lib_required_secret_files=(postgres_password.txt app_db_connection.txt credential_key.txt dp_certificate.pfx dp_certificate_password.txt production.env)

require_secret_files() {
  local name
  for name in "${lib_required_secret_files[@]}" "$@"; do
    [[ -s "$repository_root/deploy/secrets/$name" ]] || { echo "Required deployment file is missing or empty: deploy/secrets/$name" >&2; exit 1; }
  done
}

# Prints the value of an exact key in production.env; fails when the key is absent.
# The awk program is single-quoted on purpose: its $-fields belong to awk, not to the shell.
# shellcheck disable=SC2016
read_env() {
  local key="$1"
  "${sudo_cmd[@]}" awk -F= -v wanted="$key" '$1 == wanted { sub(/^[^=]*=/, ""); print; found=1; exit } END { if (!found) exit 1 }' "$environment_file"
}

# Prints the value of an exact key in production.env, or the fallback when absent.
# shellcheck disable=SC2016
read_env_or() {
  local key="$1" fallback="$2"
  "${sudo_cmd[@]}" awk -F= -v wanted="$key" -v fallback="$fallback" '$1 == wanted { sub(/^[^=]*=/, ""); print; found=1; exit } END { if (!found) print fallback }' "$environment_file"
}

# API and Worker each hold an EF pool, a separate NpgsqlDataSource and a LISTEN
# connection; without a cap they can exhaust PostgreSQL's max_connections.
warn_on_unbounded_connection_pool() {
  if ! "${sudo_cmd[@]}" grep -qi 'Maximum Pool Size=' "$repository_root/deploy/secrets/app_db_connection.txt"; then
    echo "WARNING: deploy/secrets/app_db_connection.txt has no 'Maximum Pool Size'; append ';Maximum Pool Size=25;Timeout=15;Command Timeout=120' (see docs/CI-CD-DEPLOY.md)." >&2
  fi
}

require_boolean() {
  local name="$1" value="$2"
  [[ "$value" == true || "$value" == false ]] || { echo "$name must be true or false." >&2; exit 1; }
}

# Takes a fresh backup set now and verifies it inside the backup container.
# BACKUP_FORCE=true: a deploy or rollback must not reuse the set taken earlier
# today, otherwise a failed migration could cost a whole day of orders.
run_verified_backup() {
  local backup_output backup_set backup_status=0 disk_warning
  backup_output="$("${compose[@]}" --profile operations run --rm -e BACKUP_FORCE=true backup)" || backup_status=$?
  printf '%s\n' "$backup_output"
  # backup.sh's disk guard reports a nearly full disk on this line; it becomes an operator alert.
  disk_warning="$(printf '%s\n' "$backup_output" | grep -m 1 '^DISK_GUARD_WARNING:' || true)"
  if [[ -n "$disk_warning" ]]; then
    "$repository_root/deploy/scripts/send-alert.sh" "${disk_warning#DISK_GUARD_WARNING: }" || echo "Disk warning alert could not be sent." >&2
  fi
  (( backup_status == 0 )) || { echo "Backup job failed with exit code $backup_status." >&2; return "$backup_status"; }
  backup_set="$(printf '%s\n' "$backup_output" | sed -nE 's#^Backup set created at /backup/([0-9]{8}T[0-9]{6}Z);.*$#\1#p' | tail -n 1)"
  [[ "$backup_set" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || { echo "Backup job did not return a valid backup-set name." >&2; return 1; }
  # The quoted script runs in the backup container, where its variables expand.
  # shellcheck disable=SC2016
  "${compose[@]}" --profile operations run --rm --entrypoint /bin/sh backup -ceu '
    backup_set="$1"
    cd "/backup/$backup_set"
    test -s manifest.json
    test -s database.dump
    test -s private-volumes.tar.gz
    sha256sum -c SHA256SUMS
    pg_restore --list database.dump >/dev/null
  ' sh "$backup_set"
}

# Polls /health/ready through the edge (entered via loopback) until it answers 200.
wait_for_readiness() {
  local site_address="$1" attempts="${2:-30}" status="000" attempt
  local -a resolve
  mapfile -t resolve < <(loopback_resolve_args "$site_address")
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if status="$(curl "${resolve[@]}" --connect-timeout 3 --max-time 10 --silent --show-error --fail --output /dev/null --write-out '%{http_code}' "$site_address/health/ready")" && [[ "$status" == "200" ]]; then
      return 0
    fi
    (( attempt == attempts )) || sleep 2
  done
  echo "Readiness did not return HTTP 200 after $attempts attempts; last status was $status." >&2
  return 1
}

# Waits until the worker container reports a healthy heartbeat.
wait_for_worker_health() {
  local attempts="${1:-30}" worker_id worker_health="missing" attempt
  worker_id="$("${compose[@]}" ps -q worker)"
  [[ -n "$worker_id" ]] || { echo "Worker container was not created." >&2; return 1; }
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    worker_health="$("${sudo_cmd[@]}" docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$worker_id")"
    [[ "$worker_health" == "healthy" ]] && return 0
    (( attempt == attempts )) || sleep 2
  done
  echo "Worker is not healthy: $worker_health" >&2
  return 1
}

# Confirms the SPA shell and its first JavaScript asset are served by the edge.
verify_frontend() {
  local site_address="$1" html asset_path
  html="$(curl --connect-timeout 3 --max-time 10 --silent --show-error --fail "$site_address/")"
  [[ "$html" == *'<div id="root">'* ]] || { echo "Frontend root marker was not served." >&2; return 1; }
  asset_path="$(printf '%s' "$html" | grep -oE 'src="/[^"]+\.js"' | head -1 | cut -d'"' -f2)"
  [[ -n "$asset_path" ]] || { echo "Frontend JavaScript asset could not be identified." >&2; return 1; }
  curl --connect-timeout 3 --max-time 10 --silent --show-error --fail --output /dev/null "$site_address$asset_path"
}

verify_stack() {
  local site_address="$1"
  wait_for_readiness "$site_address"
  wait_for_worker_health 30
  verify_frontend "$site_address"
}

# Deployment state shared with ci-entrypoint.sh and rollback.sh:
#   .last-success  full SHA of the last revision whose deploy finished verified
#   .hold          full SHA of a revision that was rolled back; it and its ancestors
#                  are not deployed again automatically until a newer commit lands
#   releases.tsv   one line per deploy/rollback (time, kind, revision, images)
deploy_state_dir="${RAVENCIA_DEPLOY_STATE_DIR:-$HOME/deploy-logs}"
release_log="$deploy_state_dir/releases.tsv"

record_release() {
  local kind="$1" revision="$2" app_image="$3" edge_image="$4"
  mkdir -p -- "$deploy_state_dir"
  printf '%s\t%s\t%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$kind" "$revision" "$app_image" "$edge_image" >> "$release_log"
}

record_last_success() {
  local full_revision="$1"
  mkdir -p -- "$deploy_state_dir"
  printf '%s\n' "$full_revision" > "$deploy_state_dir/.last-success.tmp"
  mv -f -- "$deploy_state_dir/.last-success.tmp" "$deploy_state_dir/.last-success"
}

# Image the running service container was created from (empty when not running).
running_service_image() {
  local container
  container="$("${sudo_cmd[@]}" docker ps -q \
    --filter label=com.docker.compose.project=marketplacehub \
    --filter "label=com.docker.compose.service=$1" | head -n 1)"
  [[ -n "$container" ]] || return 0
  "${sudo_cmd[@]}" docker inspect --format '{{.Config.Image}}' "$container"
}

# Removes manual-* app/edge tags except the ones passed as arguments (the new
# release and the one it replaced, kept for deploy/scripts/rollback.sh).
prune_manual_images_except() {
  local keep=" $* " tag
  while IFS= read -r tag; do
    [[ -n "$tag" ]] || continue
    if [[ "$keep" == *" $tag "* ]]; then
      continue
    fi
    "${sudo_cmd[@]}" docker image rm "$tag" >/dev/null 2>&1 || true
  done < <("${sudo_cmd[@]}" docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -E '^marketplacehub-(app|edge):manual-' || true)
}

# The edge serves /health/ready and /health/queues only to private source
# addresses, so host-side checks enter through loopback instead of public DNS.
loopback_resolve_args() {
  local site_address="$1" authority host port
  authority="${site_address#https://}"
  authority="${authority%%/*}"
  host="${authority%%:*}"
  port=443
  [[ "$authority" != *:* ]] || port="${authority##*:}"
  printf '%s\n' --resolve "$host:$port:127.0.0.1"
}
