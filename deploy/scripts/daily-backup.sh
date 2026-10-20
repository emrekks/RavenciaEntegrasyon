#!/usr/bin/env bash
set -Eeuo pipefail

# Nightly verified backup (ravencia-backup.timer, 20:30 UTC = 23:30 Istanbul).
# After a boot the timer may fire before the stack is up (Persistent=true), so
# the running API and Caddy containers are awaited for up to five minutes; their
# images are needed to resolve the production Compose file.

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
environment_file="$repository_root/deploy/secrets/production.env"
base_compose="$repository_root/deploy/compose/compose.yaml"
production_compose="$repository_root/deploy/compose/compose.production.yaml"

sudo_cmd=(sudo -n)
# shellcheck source=/dev/null
source "$script_dir/lib.sh"
require_secret_files

wait_seconds="${RAVENCIA_BACKUP_WAIT_SECONDS:-300}"
app_image=""
edge_image=""
for ((waited = 0; ; waited += 10)); do
  app_image="$(running_service_image api)"
  edge_image="$(running_service_image caddy)"
  [[ -z "$app_image" || -z "$edge_image" ]] || break
  (( waited < wait_seconds )) || { echo "API and Caddy containers were not running after ${wait_seconds}s; the backup was not taken." >&2; exit 1; }
  (( waited > 0 )) || echo "Waiting up to ${wait_seconds}s for the API and Caddy containers."
  sleep 10
done

external_writes_enabled="$(read_env_or MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED true)"
require_boolean MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED "$external_writes_enabled"

compose=("${sudo_cmd[@]}" env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=$external_writes_enabled" docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")
"${compose[@]}" --profile operations config --quiet
# Always a new set: the nightly restore point must include the evening, not
# reuse a set a deploy took earlier the same day.
run_verified_backup
echo "Daily backup verified."
