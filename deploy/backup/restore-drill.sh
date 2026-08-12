#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "usage: restore-drill.sh BACKUP_SET APP_IMAGE [SECRETS_ROOT] [BACKUP_VOLUME]" >&2
  exit 2
}

[[ $# -ge 2 && $# -le 4 ]] || usage
backup_set="$1"
app_image="$2"
secrets_root="${3:-deploy/secrets}"
backup_volume="${4:-marketplacehub_backup_staging}"
postgres_image="postgres:18.4@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a"

[[ "$backup_set" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || { echo "Backup set must be a UTC timestamp directory name." >&2; exit 1; }
[[ "$app_image" =~ ^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$ ]] || { echo "Application image must be immutable name@sha256." >&2; exit 1; }
[[ -d "$secrets_root" ]] || { echo "Secrets root does not exist: $secrets_root" >&2; exit 1; }
secrets_root="$(cd -- "$secrets_root" && pwd -P)"
for secret in postgres_password.txt app_db_connection.txt credential_key.txt dp_certificate.pfx dp_certificate_password.txt; do
  [[ -s "$secrets_root/$secret" ]] || { echo "Required restore smoke secret is missing: $secrets_root/$secret" >&2; exit 1; }
done

suffix="$(printf '%s' "$backup_set" | tr '[:upper:]' '[:lower:]')"
resource_prefix="marketplacehub_restore_${suffix}"
db_container="${resource_prefix}_db"
migrate_container="${resource_prefix}_migrate"
api_container="${resource_prefix}_api"
worker_container="${resource_prefix}_worker"
network="${resource_prefix}_network"
db_volume="${resource_prefix}_db_data"
private_volume="${resource_prefix}_private"
temporary_root="$(mktemp -d)"
password_file="$temporary_root/postgres_password.txt"
connection_file="$temporary_root/app_db_connection.txt"
credential_key_file="$temporary_root/credential_key.txt"
certificate_file="$temporary_root/dp_certificate.pfx"
certificate_password_file="$temporary_root/dp_certificate_password.txt"
started_at="$(date -u +%s)"

cleanup() {
  sudo docker rm -f "$worker_container" "$api_container" "$migrate_container" "$db_container" >/dev/null 2>&1 || true
  sudo docker volume rm "$private_volume" "$db_volume" >/dev/null 2>&1 || true
  sudo docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf -- "$temporary_root"
}
trap cleanup EXIT

for container in "$db_container" "$migrate_container" "$api_container" "$worker_container"; do
  ! sudo docker container inspect "$container" >/dev/null 2>&1 || { echo "Restore drill container already exists: $container" >&2; exit 1; }
done
for volume in "$db_volume" "$private_volume"; do
  ! sudo docker volume inspect "$volume" >/dev/null 2>&1 || { echo "Restore drill volume already exists: $volume" >&2; exit 1; }
done
! sudo docker network inspect "$network" >/dev/null 2>&1 || { echo "Restore drill network already exists: $network" >&2; exit 1; }
sudo docker volume inspect "$backup_volume" >/dev/null

umask 077
cp -- "$secrets_root/postgres_password.txt" "$password_file"
cp -- "$secrets_root/app_db_connection.txt" "$connection_file"
# The disposable internal server may advertise GSS encryption although the chiseled app image intentionally
# does not carry Kerberos libraries. Authentication remains SCRAM; only optional GSS transport negotiation is disabled.
printf ';GSS Encryption Mode=Disable' >> "$connection_file"
cp -- "$secrets_root/credential_key.txt" "$credential_key_file"
cp -- "$secrets_root/dp_certificate.pfx" "$certificate_file"
cp -- "$secrets_root/dp_certificate_password.txt" "$certificate_password_file"
# The temporary directory remains 0700; only these bind-mounted copies need to be readable by the non-root app user.
chmod 0444 "$password_file" "$connection_file" "$credential_key_file" "$certificate_file" "$certificate_password_file"

sudo docker network create --internal "$network" >/dev/null
sudo docker volume create "$db_volume" >/dev/null
sudo docker volume create "$private_volume" >/dev/null
echo "Restore drill: isolated resources created."

sudo docker run -d --name "$db_container" --network "$network" --network-alias postgres \
  -e POSTGRES_DB=marketplacehub \
  -e POSTGRES_USER=marketplacehub \
  -e POSTGRES_PASSWORD_FILE=/run/secrets/postgres_password \
  -v "$password_file:/run/secrets/postgres_password:ro" \
  -v "$db_volume:/var/lib/postgresql" \
  "$postgres_image" >/dev/null

for _ in $(seq 1 30); do
  if sudo docker exec "$db_container" pg_isready -U marketplacehub -d marketplacehub >/dev/null 2>&1; then break; fi
  sleep 1
done
sudo docker exec "$db_container" pg_isready -U marketplacehub -d marketplacehub >/dev/null
echo "Restore drill: isolated PostgreSQL is ready."

sudo docker run --rm \
  -v "$backup_volume:/backup:ro" \
  "$postgres_image" sh -ceu "cd '/backup/$backup_set'; sha256sum -c SHA256SUMS; test -s manifest.json; pg_restore --list database.dump >/dev/null"

sudo docker run --rm --network "$network" \
  -e PGHOST=postgres -e PGDATABASE=marketplacehub -e PGUSER=marketplacehub \
  -v "$password_file:/run/secrets/postgres_password:ro" \
  -v "$backup_volume:/backup:ro" \
  "$postgres_image" sh -ceu "export PGPASSWORD=\"\$(cat /run/secrets/postgres_password)\"; pg_restore --exit-on-error --no-owner --dbname=marketplacehub '/backup/$backup_set/database.dump'"
echo "Restore drill: database restored."

schema_count="$(sudo docker exec "$db_container" psql -U marketplacehub -d marketplacehub -Atc "select count(*) from information_schema.schemata where schema_name in ('iam','integration','ops');")"
migration_count="$(sudo docker exec "$db_container" psql -U marketplacehub -d marketplacehub -Atc 'select count(*) from public."__EFMigrationsHistory";')"
tenant_count="$(sudo docker exec "$db_container" psql -U marketplacehub -d marketplacehub -Atc 'select count(*) from iam.tenants;')"
[[ "$schema_count" == "3" ]] || { echo "Required iam/integration/ops schemas were not restored." >&2; exit 1; }
[[ "$migration_count" =~ ^[1-9][0-9]*$ ]] || { echo "Migration history is empty after restore." >&2; exit 1; }
[[ "$tenant_count" =~ ^[1-9][0-9]*$ ]] || { echo "Tenant data is empty after restore." >&2; exit 1; }
echo "Restore drill: schema and aggregate checks passed."

sudo docker run --rm -v "$backup_volume:/backup:ro" -v "$private_volume:/restore" "$postgres_image" sh -ceu "
  cd '/backup/$backup_set'
  tar -tzf private-volumes.tar.gz | awk 'BEGIN { ok=1 } /^\// { ok=0 } /(^|\/)\.\.($|\/)/ { ok=0 } END { exit ok ? 0 : 1 }'
  tar -xzf private-volumes.tar.gz -C /restore
  test -d /restore/files
  test -d /restore/dp-keys
"
echo "Restore drill: private volumes restored with safe paths."

common_args=(
  --network "$network"
  -e ASPNETCORE_ENVIRONMENT=Production
  -e MARKETPLACEHUB_ENVIRONMENT=PRODUCTION
  -e 'AllowedHosts=localhost;127.0.0.1'
  -e ConnectionStrings__AppDb_FILE=/run/secrets/app_db_connection
  -e Security__CredentialKey_FILE=/run/secrets/credential_key
  -e DataProtection__CertificatePath=/run/secrets/dp_certificate
  -e DataProtection__CertificatePassword_FILE=/run/secrets/dp_certificate_password
  -e Bootstrap__Enabled=false
  -e FeatureFlags__ExternalWrites=false
  -e Storage__Root=/var/lib/marketplacehub/files
  -e DataProtection__KeysRoot=/var/lib/marketplacehub/dp-keys
  -v "$connection_file:/run/secrets/app_db_connection:ro"
  -v "$credential_key_file:/run/secrets/credential_key:ro"
  -v "$certificate_file:/run/secrets/dp_certificate:ro"
  -v "$certificate_password_file:/run/secrets/dp_certificate_password:ro"
  -v "$private_volume:/var/lib/marketplacehub"
)

echo "Restore drill: validating migrations against restored database."
sudo docker run --name "$migrate_container" "${common_args[@]}" "$app_image" api/MarketplaceHub.Api.dll migrate || {
  sudo docker inspect --format 'migrate state: exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}}' "$migrate_container" >&2
  exit 1
}
echo "Restore drill: migration validation passed."

# The restored copy must not emit scheduled reads or Stage writes while Worker startup is tested.
sudo docker exec "$db_container" psql -U marketplacehub -d marketplacehub -v ON_ERROR_STOP=1 -c "
  update integration.connection_sync_policies set \"Enabled\" = false where \"Enabled\";
  update integration.jobs set \"Status\" = 'CANCELLED', \"CompletedAt\" = now(), \"LeaseTokenHash\" = null, \"LeaseExpiresAt\" = null, \"HeartbeatAt\" = null
  where \"Status\" in ('PENDING', 'RUNNING', 'RETRY_SCHEDULED');
"
echo "Restore drill: restored scheduler and pending jobs isolated."

sudo docker run -d --name "$api_container" "${common_args[@]}" "$app_image" api/MarketplaceHub.Api.dll >/dev/null
for _ in $(seq 1 30); do
  if sudo docker exec "$api_container" dotnet api/MarketplaceHub.Api.dll healthcheck >/dev/null 2>&1; then break; fi
  sleep 1
done
if ! sudo docker exec "$api_container" dotnet api/MarketplaceHub.Api.dll healthcheck >/dev/null 2>&1; then
  sudo docker inspect --format 'api state: status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}}' "$api_container" >&2
  sudo docker logs --tail 80 "$api_container" >&2
  exit 1
fi
echo "Restore drill: API health passed."

sudo docker run -d --name "$worker_container" "${common_args[@]}" \
  -e Worker__HealthFile=/tmp/marketplacehub-worker-heartbeat \
  "$app_image" worker/MarketplaceHub.Worker.dll >/dev/null
for _ in $(seq 1 30); do
  if sudo docker exec "$worker_container" sh -c 'test -s /tmp/marketplacehub-worker-heartbeat' >/dev/null 2>&1; then break; fi
  sleep 1
done
if ! sudo docker exec "$worker_container" sh -c 'test -s /tmp/marketplacehub-worker-heartbeat'; then
  sudo docker inspect --format 'worker state: status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}}' "$worker_container" >&2
  sudo docker logs --tail 80 "$worker_container" >&2
  exit 1
fi
echo "Restore drill: Worker health passed."

duration_seconds="$(( $(date -u +%s) - started_at ))"
printf 'Restore drill passed: backup=%s schemas=%s migrations=%s tenants=%s api=healthy worker=healthy durationSeconds=%s\n' \
  "$backup_set" "$schema_count" "$migration_count" "$tenant_count" "$duration_seconds"
