#!/usr/bin/env bash
set -Eeuo pipefail

# Registry-digest deploy (first installation and immutable-image releases).
# The day-to-day CI path is fast-deploy.sh; both share deploy/scripts/lib.sh.

validate_only=false
bootstrap=false
while (($#)); do
  case "$1" in
    --validate-only) validate_only=true; shift ;;
    --bootstrap) bootstrap=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
base_compose="$repository_root/deploy/compose/compose.yaml"
production_compose="$repository_root/deploy/compose/compose.production.yaml"
bootstrap_compose="$repository_root/deploy/compose/compose.bootstrap.yaml"
secrets_root="$repository_root/deploy/secrets"
environment_file="$secrets_root/production.env"

# The first installation runs interactively and may prompt for sudo once.
# Read by lib.sh:
# shellcheck disable=SC2034
sudo_cmd=(sudo)
# shellcheck source=/dev/null
source "$script_dir/lib.sh"
if [[ "$bootstrap" == true ]]; then
  require_secret_files bootstrap_owner_password.txt
else
  require_secret_files
fi

app_image="$(read_env MARKETPLACEHUB_APP_IMAGE)"
edge_image="$(read_env MARKETPLACEHUB_EDGE_IMAGE)"
site_address="$(read_env MARKETPLACEHUB_SITE_ADDRESS)"
external_writes_enabled="$(read_env_or MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED true)"
[[ "$app_image" =~ ^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$ ]] || { echo "Application image is not immutable." >&2; exit 1; }
[[ "$edge_image" =~ ^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$ ]] || { echo "Edge image is not immutable." >&2; exit 1; }
[[ "$site_address" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || { echo "Production site address is invalid." >&2; exit 1; }
require_boolean MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED "$external_writes_enabled"

credential_key="$(<"$secrets_root/credential_key.txt")"
decoded_bytes="$(printf '%s' "$credential_key" | base64 --decode | wc -c)"
[[ "$decoded_bytes" -eq 32 ]] || { echo "Credential key must decode to exactly 32 bytes." >&2; exit 1; }
connection="$(<"$secrets_root/app_db_connection.txt")"
for required_part in Host=postgres Database=marketplacehub Username=marketplacehub; do
  [[ "$connection" == *"$required_part"* ]] || { echo "Database connection is missing $required_part." >&2; exit 1; }
done
unset credential_key connection

compose=(sudo env "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=$external_writes_enabled" docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")

compose_version="$(sudo docker compose version --short)"
[[ "$compose_version" == "2.40.2" ]] || { echo "Exact Docker Compose 2.40.2 is required; detected $compose_version." >&2; exit 1; }
"${compose[@]}" config --quiet
warn_on_unbounded_connection_pool
warn_when_alerts_unconfigured
echo "Production configuration passed fail-closed validation."
[[ "$validate_only" == true ]] && exit 0

"${compose[@]}" pull postgres migrate api worker caddy
# A fresh set (BACKUP_FORCE=true inside run_verified_backup), never the one taken earlier today.
run_verified_backup

# Install/refresh the backup, restore-drill, health-watch and alert units.
bash "$repository_root/deploy/scripts/install-backup-timer.sh"
"${compose[@]}" up -d postgres migrate api worker caddy
if [[ "$bootstrap" == true ]]; then
  bootstrap_stack=("${compose[@]}" -f "$bootstrap_compose")
  "${bootstrap_stack[@]}" run --rm bootstrap
  rm -f -- "$secrets_root/bootstrap_owner_password.txt"
  echo "One-time bootstrap secret was removed after successful use."
fi

verify_stack "$site_address"
record_release registry "$(git -C "$repository_root" rev-parse HEAD 2>/dev/null || echo unknown)" "$app_image" "$edge_image"

"${compose[@]}" ps
echo "Deployment completed; API readiness, Worker health and frontend assets were verified."
