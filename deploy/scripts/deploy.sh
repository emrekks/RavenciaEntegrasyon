#!/usr/bin/env bash
set -Eeuo pipefail

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
secrets_root="$repository_root/deploy/secrets"
environment_file="$secrets_root/production.env"

required=(postgres_password.txt app_db_connection.txt credential_key.txt bootstrap_owner_password.txt dp_certificate.pfx dp_certificate_password.txt production.env)
for name in "${required[@]}"; do
  [[ -s "$secrets_root/$name" ]] || { echo "Required deployment file is missing or empty: deploy/secrets/$name" >&2; exit 1; }
done

read_env() {
  local key="$1"
  awk -F= -v wanted="$key" '$1 == wanted { sub(/^[^=]*=/, ""); print; found=1; exit } END { if (!found) exit 1 }' "$environment_file"
}

app_image="$(read_env MARKETPLACEHUB_APP_IMAGE)"
edge_image="$(read_env MARKETPLACEHUB_EDGE_IMAGE)"
site_address="$(read_env MARKETPLACEHUB_SITE_ADDRESS)"
[[ "$app_image" =~ ^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$ ]] || { echo "Application image is not immutable." >&2; exit 1; }
[[ "$edge_image" =~ ^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$ ]] || { echo "Edge image is not immutable." >&2; exit 1; }
[[ "$site_address" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || { echo "Production site address is invalid." >&2; exit 1; }

credential_key="$(<"$secrets_root/credential_key.txt")"
decoded_bytes="$(printf '%s' "$credential_key" | base64 --decode | wc -c)"
[[ "$decoded_bytes" -eq 32 ]] || { echo "Credential key must decode to exactly 32 bytes." >&2; exit 1; }
connection="$(<"$secrets_root/app_db_connection.txt")"
for required_part in Host=postgres Database=marketplacehub Username=marketplacehub; do
  [[ "$connection" == *"$required_part"* ]] || { echo "Database connection is missing $required_part." >&2; exit 1; }
done
unset credential_key connection

compose=(docker compose --env-file "$environment_file" -f "$base_compose" -f "$production_compose")
compose_version="$(docker compose version --short)"
[[ "$compose_version" == "2.40.2" ]] || { echo "Exact Docker Compose 2.40.2 is required; detected $compose_version." >&2; exit 1; }
"${compose[@]}" config --quiet
echo "Production configuration passed fail-closed validation."
[[ "$validate_only" == true ]] && exit 0

"${compose[@]}" pull postgres migrate api worker caddy
"${compose[@]}" up -d postgres migrate api worker caddy
if [[ "$bootstrap" == true ]]; then
  "${compose[@]}" run --rm -e Bootstrap__Enabled=true migrate api/MarketplaceHub.Api.dll bootstrap
fi

status="$(curl --silent --show-error --fail --output /dev/null --write-out '%{http_code}' "$site_address/health/ready")"
[[ "$status" == "200" ]] || { echo "Readiness returned HTTP $status." >&2; exit 1; }
"${compose[@]}" ps
echo "Deployment completed and readiness returned HTTP 200."
