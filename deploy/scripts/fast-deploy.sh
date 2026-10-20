#!/usr/bin/env bash
set -Eeuo pipefail

# Owner-managed deploy: validate the pulled revision in an isolated PostgreSQL
# container, test/build the web app, verify a backup, then migrate and deploy.

verify=true
disable_external_writes=false
enable_invoice_marketplace_delivery=false
while (($#)); do
  case "$1" in
    --verify) verify=true; shift ;;
    --disable-external-writes) disable_external_writes=true; shift ;;
    --enable-invoice-marketplace-delivery) enable_invoice_marketplace_delivery=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
environment_file="$repository_root/deploy/secrets/production.env"
base_compose="$repository_root/deploy/compose/compose.yaml"
production_compose="$repository_root/deploy/compose/compose.production.yaml"

sudo_cmd=(sudo -n)
# shellcheck source=/dev/null
source "$script_dir/lib.sh"
require_secret_files

external_writes_enabled="$(read_env_or MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED true)"
if [[ "$disable_external_writes" == true ]]; then external_writes_enabled=false; fi
require_boolean MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED "$external_writes_enabled"
# Marketplace invoice delivery follows the per-connection external write switch; production.env can still force it off.
invoice_marketplace_delivery_writes_enabled="$(read_env_or MARKETPLACEHUB_INVOICE_MARKETPLACE_DELIVERY_WRITES_ENABLED true)"
if [[ "$enable_invoice_marketplace_delivery" == true ]]; then invoice_marketplace_delivery_writes_enabled=true; fi
if [[ "$disable_external_writes" == true ]]; then invoice_marketplace_delivery_writes_enabled=false; fi
require_boolean MARKETPLACEHUB_INVOICE_MARKETPLACE_DELIVERY_WRITES_ENABLED "$invoice_marketplace_delivery_writes_enabled"

cd "$repository_root"
if [[ -n "${DEPLOY_REVISION:-}" ]]; then
  # CI passes the exact validated commit and has already fast-forwarded to it.
  [[ "$(git rev-parse HEAD)" == "$DEPLOY_REVISION" ]] || { echo "Checked-out revision does not match DEPLOY_REVISION." >&2; exit 1; }
else
  git pull --ff-only origin main
fi
full_revision="$(git rev-parse HEAD)"
revision="${full_revision:0:12}"
app_image="marketplacehub-app:manual-$revision"
edge_image="marketplacehub-edge:manual-$revision"

compose=("${sudo_cmd[@]}" env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=$external_writes_enabled" "MARKETPLACEHUB_INVOICE_MARKETPLACE_DELIVERY_WRITES_ENABLED=$invoice_marketplace_delivery_writes_enabled" docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")
# Validation must never inherit the production marketplace-write setting.
validation_compose=("${sudo_cmd[@]}" env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=false" "MARKETPLACEHUB_INVOICE_MARKETPLACE_DELIVERY_WRITES_ENABLED=false" docker compose --profile validation --env-file "$environment_file" -f "$base_compose" -f "$production_compose")

# Resolve every production and one-shot profile before building or changing
# containers. This catches missing variables, secret files, and invalid merged
# Compose configuration before the migration/deployment boundary.
"${compose[@]}" config --quiet
"${validation_compose[@]}" config --quiet
"${compose[@]}" --profile operations config --quiet
warn_on_unbounded_connection_pool

sudo -n docker build --pull=false -t "$app_image" -f Dockerfile .
sudo -n docker build --pull=false -t "$edge_image" -f deploy/caddy/Dockerfile.production .

if [[ -n "${DEPLOY_REVISION:-}" ]]; then
  # The Validate workflow already ran the same tests against this exact commit
  # on PostgreSQL 18.4, so only manual runs repeat them in the isolated stack.
  echo "Skipping isolated test run: Validate already passed for $DEPLOY_REVISION."
else
  validation_cleanup_needed=true
  cleanup_validation() {
    if [[ "$validation_cleanup_needed" == true ]]; then
      "${validation_compose[@]}" rm --stop --force --volumes validation-postgres >/dev/null 2>&1 || true
    fi
  }
  trap cleanup_validation EXIT
  "${validation_compose[@]}" run --build --rm validation-tests
  "${validation_compose[@]}" rm --stop --force --volumes validation-postgres >/dev/null
  validation_cleanup_needed=false
  trap - EXIT
fi

# Keep recent layer cache so the next deploy only rebuilds what changed. Fall
# back to dropping all builder cache only when the disk is getting tight, so an
# otherwise successful build cannot leave too little space for the backup.
free_kib="$(df --output=avail -k / | tail -n 1 | tr -d ' ')"
if (( free_kib < 15000000 )); then
  sudo -n docker builder prune --all --force
else
  sudo -n docker builder prune --all --force --filter until=168h
fi

# A fresh set (BACKUP_FORCE=true inside run_verified_backup), never the one taken
# earlier today: a broken migration must not cost the hours since that set.
run_verified_backup

# Install/refresh the backup, restore-drill, health-watch and alert units.
bash "$repository_root/deploy/scripts/install-backup-timer.sh"

# The images serving right now become the rollback target of this release.
previous_app_image="$(running_service_image api)"
previous_edge_image="$(running_service_image caddy)"

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
  verify_stack "${MARKETPLACEHUB_SITE_ADDRESS:-https://panel.ravencia.com}"
fi
record_release deploy "$full_revision" "$app_image" "$edge_image"
record_last_success "$full_revision"

# Keep this release and the one it replaced (N-1, for deploy/scripts/rollback.sh);
# remove older manual-* tags. Docker refuses removal while a container still
# references an image, so active containers stay recoverable.
prune_manual_images_except "$app_image" "$edge_image" "$previous_app_image" "$previous_edge_image"
if [[ "$previous_app_image" == marketplacehub-app:manual-* && "$previous_app_image" != "$app_image" ]]; then
  echo "Rollback target kept: $previous_app_image / $previous_edge_image (deploy/scripts/rollback.sh ${previous_app_image##*:manual-})"
fi

echo "Fast deploy completed: $revision"
