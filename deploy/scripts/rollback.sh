#!/usr/bin/env bash
set -Eeuo pipefail

# Code-only rollback: run the API, Worker and edge of an earlier revision against
# the current database. It never runs migrations and never touches PostgreSQL
# data; restoring the database is a separate, manual decision
# (docs/RUNBOOK-GERI-ALMA.md).
#
# Usage: deploy/scripts/rollback.sh <revision> [--build] [--skip-backup] [--allow-schema-drift]
#   <revision>            commit SHA (12+ hex characters) to run again
#   --build               build the images from that revision when its manual-* tags are gone
#   --skip-backup         do not take the fresh verified backup first (only when the database is down)
#   --allow-schema-drift  proceed although migrations were added after <revision>

usage() {
  sed -n '9,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

target=""
build=false
skip_backup=false
allow_schema_drift=false
while (($#)); do
  case "$1" in
    --build) build=true; shift ;;
    --skip-backup) skip_backup=true; shift ;;
    --allow-schema-drift) allow_schema_drift=true; shift ;;
    -h|--help) usage ;;
    -*) echo "Unknown argument: $1" >&2; usage ;;
    *) [[ -z "$target" ]] || usage; target="$1"; shift ;;
  esac
done
[[ "$target" =~ ^[0-9a-f]{12,40}$ ]] || usage

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
environment_file="$repository_root/deploy/secrets/production.env"
base_compose="$repository_root/deploy/compose/compose.yaml"
production_compose="$repository_root/deploy/compose/compose.production.yaml"
migrations_path="src/MarketplaceHub.Infrastructure/Persistence/Migrations"

sudo_cmd=(sudo -n)
# shellcheck source=/dev/null
source "$script_dir/lib.sh"
require_secret_files

cd "$repository_root"
full_target="$(git rev-parse --verify --quiet "$target^{commit}")" || { echo "Unknown revision: $target" >&2; exit 1; }
current_head="$(git rev-parse HEAD)"
git merge-base --is-ancestor "$full_target" "$current_head" || { echo "Revision $target is not part of the checked-out main history." >&2; exit 1; }
short_target="${full_target:0:12}"
app_image="marketplacehub-app:manual-$short_target"
edge_image="marketplacehub-edge:manual-$short_target"

# Migrations are forward-only. Old code against a newer schema is safe only when
# the newer migrations were additive; otherwise the database must be restored.
if ! git diff --quiet "$full_target" "$current_head" -- "$migrations_path"; then
  echo "Migrations changed between $short_target and ${current_head:0:12}:" >&2
  git diff --stat "$full_target" "$current_head" -- "$migrations_path" >&2
  if [[ "$allow_schema_drift" == false ]]; then
    echo "Refusing a code-only rollback. Check that the new migrations are additive (new tables/nullable columns) and rerun with --allow-schema-drift, or follow the database restore section of docs/RUNBOOK-GERI-ALMA.md." >&2
    exit 1
  fi
  echo "Proceeding with --allow-schema-drift: the old code will run against the newer schema." >&2
fi

image_exists() { "${sudo_cmd[@]}" docker image inspect "$1" >/dev/null 2>&1; }
if ! image_exists "$app_image" || ! image_exists "$edge_image"; then
  [[ "$build" == true ]] || { echo "Images $app_image / $edge_image are not on this host (only the previous release is kept). Rerun with --build to build them from $short_target." >&2; exit 1; }
  build_root="$(mktemp -d)"
  trap 'rm -rf -- "$build_root"' EXIT
  # Build from an exported tree so the checkout (and the CI fast-forward path) stays untouched.
  git archive "$full_target" | tar -x -C "$build_root"
  "${sudo_cmd[@]}" docker build --pull=false -t "$app_image" -f "$build_root/Dockerfile" "$build_root"
  "${sudo_cmd[@]}" docker build --pull=false -t "$edge_image" -f "$build_root/deploy/caddy/Dockerfile.production" "$build_root"
fi

external_writes_enabled="$(read_env_or MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED true)"
require_boolean MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED "$external_writes_enabled"
invoice_marketplace_delivery_writes_enabled="$(read_env_or MARKETPLACEHUB_INVOICE_MARKETPLACE_DELIVERY_WRITES_ENABLED true)"
require_boolean MARKETPLACEHUB_INVOICE_MARKETPLACE_DELIVERY_WRITES_ENABLED "$invoice_marketplace_delivery_writes_enabled"

# The current compose files are used on purpose: secrets, networks and volumes
# must stay those of the running stack; only the images change.
compose=("${sudo_cmd[@]}" env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=$external_writes_enabled" "MARKETPLACEHUB_INVOICE_MARKETPLACE_DELIVERY_WRITES_ENABLED=$invoice_marketplace_delivery_writes_enabled" docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")
"${compose[@]}" config --quiet

if [[ "$skip_backup" == false ]]; then
  run_verified_backup
fi

replaced_app_image="$(running_service_image api)"
echo "Rolling back from ${replaced_app_image:-<none>} to $app_image (no migration is run)."
# --no-deps: the one-shot migrate service must not run with the older code.
"${compose[@]}" up -d --no-build --no-deps api worker caddy
verify_stack "${MARKETPLACEHUB_SITE_ADDRESS:-https://panel.ravencia.com}"

record_release rollback "$full_target" "$app_image" "$edge_image"
record_last_success "$full_target"
# Scheduled/automatic deploys must not bring the rolled-back revision back.
# deploy_state_dir is set by lib.sh:
# shellcheck disable=SC2154
hold_file="$deploy_state_dir/.hold"
if [[ "$current_head" != "$full_target" ]]; then
  printf '%s\n' "$current_head" > "$hold_file"
  echo "Automatic deploys of ${current_head:0:12} and older are held until a newer commit is deployed ($hold_file)."
fi
echo "Rollback completed: $short_target is serving."
