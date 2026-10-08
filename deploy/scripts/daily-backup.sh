#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
environment_file="$repository_root/deploy/secrets/production.env"
base_compose="$repository_root/deploy/compose/compose.yaml"
production_compose="$repository_root/deploy/compose/compose.production.yaml"

for required in postgres_password.txt app_db_connection.txt credential_key.txt dp_certificate.pfx dp_certificate_password.txt production.env; do
  [[ -s "$repository_root/deploy/secrets/$required" ]] || { echo "Required deployment file is missing: deploy/secrets/$required" >&2; exit 1; }
done

service_container_id() {
  sudo -n docker ps -q \
    --filter label=com.docker.compose.project=marketplacehub \
    --filter "label=com.docker.compose.service=$1"
}

app_container="$(service_container_id api)"
edge_container="$(service_container_id caddy)"
[[ -n "$app_container" && -n "$edge_container" ]] || { echo "Healthy API and Caddy containers are required to resolve the current Compose images." >&2; exit 1; }
app_image="$(sudo -n docker inspect --format '{{.Config.Image}}' "$app_container")"
edge_image="$(sudo -n docker inspect --format '{{.Config.Image}}' "$edge_container")"
external_writes_enabled="$(sudo -n awk -F= '$1 == "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED" { sub(/^[^=]*=/, ""); print; found=1; exit } END { if (!found) print "true" }' "$environment_file")"
[[ "$external_writes_enabled" == true || "$external_writes_enabled" == false ]] || { echo "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED must be true or false." >&2; exit 1; }

compose=(sudo -n env "MARKETPLACEHUB_APP_IMAGE=$app_image" "MARKETPLACEHUB_EDGE_IMAGE=$edge_image" "MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=$external_writes_enabled" docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")
"${compose[@]}" --profile operations config --quiet
backup_output="$("${compose[@]}" --profile operations run --rm backup)"
printf '%s\n' "$backup_output"
backup_set="$(printf '%s\n' "$backup_output" | sed -nE 's#^Backup set created at /backup/([0-9]{8}T[0-9]{6}Z);.*$#\1#p' | tail -n 1)"
[[ "$backup_set" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || { echo "Backup job did not return a valid backup-set name." >&2; exit 1; }

"${compose[@]}" --profile operations run --rm --entrypoint /bin/sh backup -ceu '
  cd "/backup/$1"
  test -s manifest.json
  test -s database.dump
  test -s private-volumes.tar.gz
  sha256sum -c SHA256SUMS
  pg_restore --list database.dump >/dev/null
' sh "$backup_set"
echo "Daily backup verified: $backup_set"
