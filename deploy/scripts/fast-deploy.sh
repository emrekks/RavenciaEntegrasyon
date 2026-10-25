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

# Migration rehearsal: CI only migrates an empty database, so a data-repair
# migration would otherwise meet real rows for the first time in production. The
# new image's migrate runs first against a disposable restore of the backup set
# taken above (deploy/backup/restore-drill.sh --migration-only, isolated network
# and volumes). It costs a full pg_restore plus the migration (minutes), so it
# stays off the common path:
#   auto (default)  only when the migrations folder changed since the running
#                   release (or that release is unknown);
#   always / never  regardless. RAVENCIA_MIGRATION_REHEARSAL, else production.env's
#                   MARKETPLACEHUB_MIGRATION_REHEARSAL, else auto.
# A failed or timed-out rehearsal (RAVENCIA_MIGRATION_REHEARSAL_TIMEOUT_SECONDS,
# default 1200) stops the deploy before production is touched. It is skipped
# with a warning when the root disk is above 80%: the restore needs a second
# copy of the database.
migration_rehearsal="${RAVENCIA_MIGRATION_REHEARSAL:-$(read_env_or MARKETPLACEHUB_MIGRATION_REHEARSAL auto)}"
rehearsal_timeout="${RAVENCIA_MIGRATION_REHEARSAL_TIMEOUT_SECONDS:-1200}"
[[ "$migration_rehearsal" =~ ^(auto|always|never)$ ]] || { echo "Migration rehearsal must be auto, always or never (got '$migration_rehearsal')." >&2; exit 1; }
[[ "$rehearsal_timeout" =~ ^[1-9][0-9]*$ ]] || { echo "RAVENCIA_MIGRATION_REHEARSAL_TIMEOUT_SECONDS must be a positive integer." >&2; exit 1; }
rehearse=false
case "$migration_rehearsal" in
  always) rehearse=true ;;
  never) echo "Migration rehearsal skipped (never)." ;;
  auto)
    running_revision=""
    [[ "$previous_app_image" != marketplacehub-app:manual-* ]] || running_revision="${previous_app_image##*:manual-}"
    if [[ -z "$running_revision" ]] || ! running_full="$(git rev-parse --verify --quiet "$running_revision^{commit}")"; then
      echo "Migration rehearsal: the running release is unknown (${previous_app_image:-none}); rehearsing."
      rehearse=true
    elif ! git diff --quiet "$running_full" "$full_revision" -- src/MarketplaceHub.Infrastructure/Persistence/Migrations; then
      echo "Migration rehearsal: migrations changed since $running_revision; rehearsing."
      rehearse=true
    else
      echo "Migration rehearsal skipped: no migration changed since $running_revision."
    fi
    ;;
esac
# verified_backup_set is assigned by run_verified_backup (lib.sh).
# shellcheck disable=SC2154
if [[ "$rehearse" == true ]]; then
  root_disk_percent="$(df --output=pcent / | tail -n 1 | tr -dc '0-9')"
  if (( ${root_disk_percent:-100} > 80 )); then
    echo "::warning::Migration rehearsal skipped: the root disk is ${root_disk_percent:-?}% full and the restore needs a second database copy."
  else
    app_image_id="$(sudo -n docker image inspect --format '{{.Id}}' "$app_image")"
    echo "Migration rehearsal: backup set $verified_backup_set, image $app_image_id, up to ${rehearsal_timeout}s."
    rehearsal_status=0
    timeout --kill-after=120 "$rehearsal_timeout" "${sudo_cmd[@]}" bash "$repository_root/deploy/backup/restore-drill.sh" --migration-only \
      "$verified_backup_set" "$app_image_id" "$repository_root/deploy/secrets" marketplacehub_backup_staging || rehearsal_status=$?
    if (( rehearsal_status == 124 || rehearsal_status == 137 )); then
      echo "Migration rehearsal did not finish within ${rehearsal_timeout}s; production was not migrated. The migration is likely to run that long in production too: plan a window, then deploy by hand with RAVENCIA_MIGRATION_REHEARSAL=never (or raise the timeout)." >&2
      exit 1
    elif (( rehearsal_status != 0 )); then
      echo "Migration rehearsal failed against the restored copy of $verified_backup_set (exit code $rehearsal_status); production was not migrated." >&2
      exit 1
    fi
  fi
fi

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
