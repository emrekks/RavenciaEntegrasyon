#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

app_image=""
edge_image=""
site_address=""
owner_email=""
owner_password_file=""

while (($#)); do
  case "$1" in
    --app-image) app_image="${2:?missing --app-image value}"; shift 2 ;;
    --edge-image) edge_image="${2:?missing --edge-image value}"; shift 2 ;;
    --site-address) site_address="${2:?missing --site-address value}"; shift 2 ;;
    --owner-email) owner_email="${2:?missing --owner-email value}"; shift 2 ;;
    --owner-password-file) owner_password_file="${2:?missing --owner-password-file value}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
secrets_root="$repository_root/deploy/secrets"

require_single_line() {
  local name="$1" value="$2"
  [[ -n "$value" && "$value" != *$'\n'* && "$value" != *$'\r'* ]] || {
    echo "$name must be a non-empty single-line value." >&2
    exit 1
  }
}

require_single_line "Application image" "$app_image"
require_single_line "Edge image" "$edge_image"
require_single_line "Owner email" "$owner_email"
[[ "$app_image" =~ ^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$ ]] || { echo "Application image must be immutable name@sha256." >&2; exit 1; }
[[ "$edge_image" =~ ^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$ ]] || { echo "Edge image must be immutable name@sha256." >&2; exit 1; }
[[ "$site_address" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || { echo "Site address must be an HTTPS origin without a path or query." >&2; exit 1; }
[[ "$owner_email" =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]] || { echo "Owner email format is invalid." >&2; exit 1; }

targets=(
  postgres_password.txt
  app_db_connection.txt
  credential_key.txt
  bootstrap_owner_password.txt
  dp_certificate.pfx
  dp_certificate_password.txt
  dp_certificate_metadata.txt
  production.env
)
for name in "${targets[@]}"; do
  [[ ! -e "$secrets_root/$name" ]] || { echo "Deployment secrets already exist; initializer will not overwrite them." >&2; exit 1; }
done

if [[ -n "$owner_password_file" ]]; then
  [[ -f "$owner_password_file" ]] || { echo "Owner password file does not exist." >&2; exit 1; }
  owner_password="$(<"$owner_password_file")"
else
  read -r -s -p "Temporary Owner password (15-64 characters; upper/lower/digit/symbol): " owner_password
  printf '\n'
  read -r -s -p "Repeat temporary Owner password: " owner_password_confirm
  printf '\n'
  [[ "$owner_password" == "$owner_password_confirm" ]] || { echo "Owner passwords do not match." >&2; exit 1; }
  unset owner_password_confirm
fi

(( ${#owner_password} >= 15 && ${#owner_password} <= 64 )) || { echo "Owner password length is invalid." >&2; exit 1; }
[[ "$owner_password" =~ [A-Z] && "$owner_password" =~ [a-z] && "$owner_password" =~ [0-9] && "$owner_password" =~ [^A-Za-z0-9] ]] || {
  echo "Owner password does not satisfy the bootstrap policy." >&2
  exit 1
}

mkdir -p -- "$secrets_root"
chmod 700 -- "$secrets_root"
staging_root="$(mktemp -d "$secrets_root/.deployment-staging.XXXXXX")"
certificate_tmp="$staging_root/certificate"
mkdir -- "$certificate_tmp"
cleanup() {
  rm -rf -- "$staging_root"
  unset owner_password postgres_password credential_key certificate_password
}
trap cleanup EXIT

postgres_password="$(openssl rand -base64 32 | tr -d '\r\n')"
credential_key="$(openssl rand -base64 32 | tr -d '\r\n')"
certificate_password="$(openssl rand -base64 32 | tr -d '\r\n')"

printf '%s' "$postgres_password" > "$staging_root/postgres_password.txt"
printf 'Host=postgres;Port=5432;Database=marketplacehub;Username=marketplacehub;Password=%s' "$postgres_password" > "$staging_root/app_db_connection.txt"
printf '%s' "$credential_key" > "$staging_root/credential_key.txt"
printf '%s' "$owner_password" > "$staging_root/bootstrap_owner_password.txt"
printf '%s' "$certificate_password" > "$staging_root/dp_certificate_password.txt"

openssl req -x509 -newkey rsa:3072 -sha256 -nodes \
  -subj '/CN=MarketplaceHub Data Protection' \
  -days 1095 \
  -keyout "$certificate_tmp/private.key" \
  -out "$certificate_tmp/certificate.crt" >/dev/null 2>&1
openssl pkcs12 -export \
  -inkey "$certificate_tmp/private.key" \
  -in "$certificate_tmp/certificate.crt" \
  -out "$staging_root/dp_certificate.pfx" \
  -passout "file:$staging_root/dp_certificate_password.txt" >/dev/null 2>&1
openssl pkcs12 -info -noout \
  -in "$staging_root/dp_certificate.pfx" \
  -passin "file:$staging_root/dp_certificate_password.txt" >/dev/null 2>&1
{
  openssl x509 -in "$certificate_tmp/certificate.crt" -noout -subject -fingerprint -sha256 -enddate
} > "$staging_root/dp_certificate_metadata.txt"

cat > "$staging_root/production.env" <<EOF
MARKETPLACEHUB_APP_IMAGE=$app_image
MARKETPLACEHUB_EDGE_IMAGE=$edge_image
MARKETPLACEHUB_SITE_ADDRESS=$site_address
MARKETPLACEHUB_BOOTSTRAP_TENANT_CODE=ravencia
MARKETPLACEHUB_BOOTSTRAP_TENANT_NAME=Ravencia
MARKETPLACEHUB_BOOTSTRAP_OWNER_EMAIL=$owner_email
MARKETPLACEHUB_BOOTSTRAP_OWNER_NAME=Ravencia Admin
EOF
chmod 600 -- "$staging_root"/*.txt "$staging_root"/*.pfx "$staging_root"/*.env
for name in "${targets[@]}"; do
  mv -- "$staging_root/$name" "$secrets_root/$name"
done

echo "Ubuntu deployment secrets and Data Protection PFX were created without printing secret values."
echo "Back up the PFX, its password and metadata to the approved encrypted off-host secret target."
