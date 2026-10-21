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
  # Same 23:45-00:30 rule as ci-entrypoint.sh, for a server whose root-owned copy of
  # that script predates it. Nothing is recorded, so the morning run deploys this
  # revision (it is already checked out). A manual run is never deferred.
  # shellcheck source=/dev/null
  source "$script_dir/ci-entrypoint.sh"
  if deploy_window_closed; then
    echo "::warning::Deploy of $DEPLOY_REVISION deferred to morning: no deploy starts between 23:45 and the 00:30 nightly stop (Europe/Istanbul)."
    exit 0
  fi
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
warn_when_alerts_unconfigured

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
# A data migration can legitimately run for minutes; each statement is bounded by
# the migrate command's own statement_timeout (20 min) and lock_timeout (15 s,
# retried), so the wait here is only the outer bound. On timeout the container is
# stopped: its open transaction rolls back and the old stack keeps serving the
# schema of the last completed migration.
migration_timeout="${RAVENCIA_MIGRATION_TIMEOUT_SECONDS:-1800}"
[[ "$migration_timeout" =~ ^[1-9][0-9]*$ ]] || { echo "RAVENCIA_MIGRATION_TIMEOUT_SECONDS must be a positive integer." >&2; exit 1; }
echo "Waiting up to ${migration_timeout}s for the database migration."
migration_started="$(date +%s)"
migration_exit_code=""
wait_status=0
migration_exit_code="$(timeout "$migration_timeout" sudo -n docker wait "$migrate_id")" || wait_status=$?
migration_seconds="$(( $(date +%s) - migration_started ))"
if (( wait_status == 124 )); then
  echo "Database migration did not finish within ${migration_timeout}s; stopping the migrate container (the old stack keeps serving)." >&2
  sudo -n docker stop --time 30 "$migrate_id" >/dev/null || true
  sudo -n docker logs --tail 40 "$migrate_id" >&2 || true
  exit 1
fi
[[ "$wait_status" == "0" && "$migration_exit_code" == "0" ]] || {
  migration_status="$(sudo -n docker inspect --format '{{.State.Status}}' "$migrate_id" 2>/dev/null || echo unknown)"
  echo "Database migration did not complete successfully: state=$migration_status exit_code=${migration_exit_code:-unknown} after ${migration_seconds}s." >&2
  sudo -n docker logs --tail 40 "$migrate_id" >&2 || true
  exit 1
}
echo "Database migration completed in ${migration_seconds}s."

# Automatic rollback (RAVENCIA_AUTO_ROLLBACK, default true): when the new release
# does not start or fails verify_stack, the previous release's images are run
# again through rollback.sh -- but only when that is a pure code rollback:
#   - the previous release is a kept manual-<rev> app+edge pair of the same revision,
#     older than this one;
#   - no file under the migrations folder changed between it and this revision
#     (old code against a newer schema is a manual decision, RUNBOOK-GERI-ALMA.md);
#   - the deploy ran with production.env's write settings (rollback.sh reads those).
# rollback.sh skips its own backup: the verified set above is minutes old and the
# schema did not change. It also holds this revision from automatic redeploys.
auto_rollback="${RAVENCIA_AUTO_ROLLBACK:-true}"
rollback_target=""
attempt_automatic_rollback() {
  local previous_full
  if [[ "$auto_rollback" != true ]]; then
    echo "Automatic rollback is disabled (RAVENCIA_AUTO_ROLLBACK=$auto_rollback)." >&2
    return 1
  fi
  if [[ "$disable_external_writes" == true || "$enable_invoice_marketplace_delivery" == true ]]; then
    echo "Automatic rollback skipped: this deploy overrode the write settings, which rollback.sh would not keep." >&2
    return 1
  fi
  if [[ "$previous_app_image" != marketplacehub-app:manual-* || "$previous_edge_image" != marketplacehub-edge:manual-* ]]; then
    echo "Automatic rollback skipped: no previous manual-* release was running (api=${previous_app_image:-none}, caddy=${previous_edge_image:-none})." >&2
    return 1
  fi
  rollback_target="${previous_app_image##*:manual-}"
  if [[ ! "$rollback_target" =~ ^[0-9a-f]{12}$ || "${previous_edge_image##*:manual-}" != "$rollback_target" ]]; then
    echo "Automatic rollback skipped: previous app and edge images are not one release ($previous_app_image, $previous_edge_image)." >&2
    return 1
  fi
  if [[ "$rollback_target" == "$revision" ]]; then
    echo "Automatic rollback skipped: the failed release is the one that was already running (redeploy)." >&2
    return 1
  fi
  previous_full="$(git rev-parse --verify --quiet "$rollback_target^{commit}")" || {
    echo "Automatic rollback skipped: revision $rollback_target is not in the local history." >&2
    return 1
  }
  if ! git diff --quiet "$previous_full" "$full_revision" -- src/MarketplaceHub.Infrastructure/Persistence/Migrations; then
    echo "Automatic rollback skipped: migrations changed between $rollback_target and $revision; follow docs/RUNBOOK-GERI-ALMA.md." >&2
    return 1
  fi
  echo "Automatic rollback: returning to $rollback_target (code only; the schema is unchanged)." >&2
  rollback_attempted=true
  bash "$script_dir/rollback.sh" "$rollback_target" --skip-backup
}
rollback_attempted=false

site_address="${MARKETPLACEHUB_SITE_ADDRESS:-https://panel.ravencia.com}"
release_status=0
"${compose[@]}" up -d --no-build api worker caddy || release_status=$?
if (( release_status == 0 )) && [[ "$verify" == true ]]; then
  verify_stack "$site_address" || release_status=$?
fi
if (( release_status != 0 )); then
  echo "Release $revision did not start or failed verification (exit code $release_status)." >&2
  if attempt_automatic_rollback; then
    "$script_dir/send-alert.sh" "Deploy of $revision failed verification and was rolled back automatically to $rollback_target; $revision is held from automatic redeploys. Log: ~/deploy-logs/" \
      || echo "Rollback alert could not be sent." >&2
    echo "Deploy failed; the previous release $rollback_target is serving again." >&2
  elif [[ "$rollback_attempted" == true ]]; then
    "$script_dir/send-alert.sh" "Deploy of $revision failed verification and the automatic rollback to $rollback_target FAILED too; act now: ~/deploy-logs/, docs/RUNBOOK-GERI-ALMA.md." \
      || echo "Failure alert could not be sent." >&2
    echo "Deploy failed and the automatic rollback to $rollback_target failed; see docs/RUNBOOK-GERI-ALMA.md." >&2
  else
    "$script_dir/send-alert.sh" "Deploy of $revision failed verification and was NOT rolled back automatically; check ~/deploy-logs/ and docs/RUNBOOK-GERI-ALMA.md." \
      || echo "Failure alert could not be sent." >&2
    echo "Deploy failed and the stack was left as it is; see docs/RUNBOOK-GERI-ALMA.md." >&2
  fi
  exit 1
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
