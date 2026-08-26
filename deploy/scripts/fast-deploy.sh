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
"${compose[@]}" up -d --no-build postgres migrate api worker caddy

if [[ "$verify" == true ]]; then
  site_address="$(sudo -n awk -F= '$1 == "MARKETPLACEHUB_SITE_ADDRESS" { sub(/^[^=]*=/, ""); print; exit }' "$environment_file" 2>/dev/null || true)"
  site_address="${site_address:-https://panel.ravencia.com}"
  curl --connect-timeout 3 --max-time 10 --silent --show-error --fail "$site_address/health/ready"
  echo
fi

echo "Fast deploy completed: $revision"
