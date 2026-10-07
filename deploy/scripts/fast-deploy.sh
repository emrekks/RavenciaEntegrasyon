#!/usr/bin/env bash
set -Eeuo pipefail

# Owner-managed deploy: validate the pulled revision in an isolated PostgreSQL
# container, test/build the web app, verify a backup, then migrate and deploy.

verify=true
while (($#)); do
  case "$1" in
    --verify) verify=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
environment_file="$repository_root/deploy/secrets/production.env"
base_compose="$repository_root/deploy/compose/compose.yaml"
production_compose="$repository_root/deploy/compose/compose.production.yaml"

for required in postgres_password.txt app_db_connection.txt credential_key.txt dp_certificate.pfx dp_certificate_password.txt production.env; do
  [[ -s "$repository_root/deploy/secrets/$required" ]] || { echo "Required deployment file is missing: deploy/secrets/$required" >&2; exit 1; }
done

read_env() {
  local key="$1"
  awk -F= -v wanted="$key" '$1 == wanted { sub(/^[^=]*=/, ""); print; found=1; exit } END { if (!found) exit 1 }' "$environment_file"
}
external_writes_enabled="$(read_env MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED || printf '%s' true)"
[[ "$external_writes_enabled" == true || "$external_writes_enabled" == false ]] || { echo "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED must be true or false." >&2; exit 1; }

cd "$repository_root"
git pull --ff-only origin main
revision="$(git rev-parse --short=12 HEAD)"
app_image="marketplacehub-app:manual-$revision"
edge_image="marketplacehub-edge:manual-$revision"

sudo -n docker build --pull=false -t "$app_image" -f Dockerfile .
sudo -n docker build --pull=false -t "$edge_image" -f deploy/caddy/Dockerfile.production .

compose=(sudo -n env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=$external_writes_enabled" docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")

validation_compose=(sudo -n env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=$external_writes_enabled" docker compose --profile validation --env-file "$environment_file" -f "$base_compose" -f "$production_compose")
validation_cleanup_needed=true
cleanup_validation() {
  if [[ "$validation_cleanup_needed" == true ]]; then
    "${validation_compose[@]}" rm --stop --force validation-postgres >/dev/null 2>&1 || true
  fi
}
trap cleanup_validation EXIT
"${validation_compose[@]}" run --build --rm validation-tests
"${validation_compose[@]}" rm --stop --force validation-postgres >/dev/null
validation_cleanup_needed=false
trap - EXIT

run_verified_backup() {
  local backup_output backup_set
  backup_output="$("${compose[@]}" --profile operations run --rm backup)"
  printf '%s\n' "$backup_output"
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
run_verified_backup

# Keep the currently serving API/worker/edge alive until the new database
# migration has completed successfully. Compose's depends_on condition also
# protects a fresh stack, but starting the one-shot migration separately makes
# the failure boundary explicit and avoids replacing healthy application
# containers with a stack that can never become ready.
"${compose[@]}" up -d --no-build postgres migrate
migrate_id="$("${compose[@]}" ps -q migrate)"
[[ -n "$migrate_id" ]] || { echo "Migration container was not created." >&2; exit 1; }
migration_status=""
migration_exit_code=""
for ((attempt = 1; attempt <= 120; attempt++)); do
  migration_status="$(sudo -n docker inspect --format '{{.State.Status}}' "$migrate_id")"
  if [[ "$migration_status" == "exited" ]]; then
    migration_exit_code="$(sudo -n docker inspect --format '{{.State.ExitCode}}' "$migrate_id")"
    break
  fi
  sleep 1
done
[[ "$migration_status" == "exited" && "$migration_exit_code" == "0" ]] || {
  echo "Database migration did not complete successfully: state=$migration_status exit_code=${migration_exit_code:-unknown}." >&2
  exit 1
}

"${compose[@]}" up -d --no-build api worker caddy

if [[ "$verify" == true ]]; then
  site_address="${MARKETPLACEHUB_SITE_ADDRESS:-https://panel.ravencia.com}"
  status="000"
  readiness_attempts=30
  for ((attempt = 1; attempt <= readiness_attempts; attempt++)); do
    if status="$(curl --connect-timeout 3 --max-time 10 --silent --show-error --fail --output /dev/null --write-out '%{http_code}' "$site_address/health/ready")" && [[ "$status" == "200" ]]; then
      break
    fi
    (( attempt == readiness_attempts )) || sleep 2
  done
  [[ "$status" == "200" ]] || { echo "Readiness did not return HTTP 200 after $readiness_attempts attempts; last status was $status." >&2; exit 1; }

  worker_id="$("${compose[@]}" ps -q worker)"
  [[ -n "$worker_id" ]] || { echo "Worker container was not created." >&2; exit 1; }
  worker_health="missing"
  for ((attempt = 1; attempt <= 30; attempt++)); do
    worker_health="$(sudo -n docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$worker_id")"
    [[ "$worker_health" == "healthy" ]] && break
    (( attempt == 30 )) || sleep 2
  done
  [[ "$worker_health" == "healthy" ]] || { echo "Worker is not healthy: $worker_health" >&2; exit 1; }

  html="$(curl --connect-timeout 3 --max-time 10 --silent --show-error --fail "$site_address/")"
  [[ "$html" == *'<div id="root">'* ]] || { echo "Frontend root marker was not served." >&2; exit 1; }
  asset_path="$(printf '%s' "$html" | grep -oE 'src="/[^"]+\.js"' | head -1 | cut -d'"' -f2)"
  [[ -n "$asset_path" ]] || { echo "Frontend JavaScript asset could not be identified." >&2; exit 1; }
  curl --connect-timeout 3 --max-time 10 --silent --show-error --fail --output /dev/null "$site_address$asset_path"
fi

# Keep the current manual images and remove only older tags produced by this
# script. Docker refuses removal while a stopped container still references an
# image, so this remains recoverable for active containers.
for old_image in $(sudo -n docker image ls --format '{{.Repository}}:{{.Tag}}' | awk -v app="$app_image" -v edge="$edge_image" '$0 ~ /^marketplacehub-(app|edge):manual-/ && $0 != app && $0 != edge'); do
  sudo -n docker image rm "$old_image" >/dev/null 2>&1 || true
done

echo "Fast deploy completed: $revision"
