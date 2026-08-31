#!/usr/bin/env bash
set -Eeuo pipefail

# Fast owner-managed deploy: pull main, reuse Docker layer caches, and replace
# only the application stack. It intentionally does not run tests or a backup.
# Use deploy.sh for the immutable release / backup workflow when requested.

verify=false
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

cd "$repository_root"
git pull --ff-only origin main
revision="$(git rev-parse --short=12 HEAD)"
app_image="marketplacehub-app:manual-$revision"
edge_image="marketplacehub-edge:manual-$revision"

sudo -n docker build --pull=false -t "$app_image" -f Dockerfile .
sudo -n docker build --pull=false -t "$edge_image" -f deploy/caddy/Dockerfile.production .

compose=(sudo -n env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")

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
  worker_health="$(sudo -n docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$worker_id")"
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
