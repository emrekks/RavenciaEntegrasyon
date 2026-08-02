#!/usr/bin/env bash
set -Eeuo pipefail

deploy=false
bootstrap=false
app_image=""
edge_image=""
site_address=""
owner_email=""
owner_password_file=""

while (($#)); do
  case "$1" in
    --deploy) deploy=true; shift ;;
    --bootstrap) bootstrap=true; shift ;;
    --app-image) app_image="${2:?missing --app-image value}"; shift 2 ;;
    --edge-image) edge_image="${2:?missing --edge-image value}"; shift 2 ;;
    --site-address) site_address="${2:?missing --site-address value}"; shift 2 ;;
    --owner-email) owner_email="${2:?missing --owner-email value}"; shift 2 ;;
    --owner-password-file) owner_password_file="${2:?missing --owner-password-file value}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ "$bootstrap" == false || "$deploy" == true ]] || { echo "--bootstrap requires --deploy." >&2; exit 2; }

for command in bash curl openssl sha256sum base64 awk docker systemctl; do
  command -v "$command" >/dev/null 2>&1 || { echo "Required command is missing: $command" >&2; exit 1; }
done

[[ -r /etc/os-release ]] || { echo "Ubuntu identity cannot be verified." >&2; exit 1; }
# shellcheck disable=SC1091
source /etc/os-release
[[ "${ID:-}" == "ubuntu" && "${VERSION_ID:-}" == "24.04" ]] || { echo "Ubuntu Server 24.04 LTS is required." >&2; exit 1; }
architecture="$(uname -m)"
[[ "$architecture" == "x86_64" ]] || { echo "x86_64 host is required; detected $architecture." >&2; exit 1; }

cpu_count="$(nproc)"
memory_kib="$(awk '/MemTotal/ { print $2 }' /proc/meminfo)"
filesystem_bytes="$(df -B1 --output=size "$(pwd -P)" | tail -1 | tr -d ' ')"
(( cpu_count >= 4 )) || { echo "At least 4 vCPU are required; detected $cpu_count." >&2; exit 1; }
(( memory_kib >= 7500000 )) || { echo "At least 8 GB RAM are required." >&2; exit 1; }
(( filesystem_bytes >= 90000000000 )) || { echo "A 100-120 GB NVMe target filesystem is required." >&2; exit 1; }

systemctl is-enabled --quiet docker || { echo "Docker Engine must be enabled in systemd." >&2; exit 1; }
systemctl is-active --quiet docker || { echo "Docker Engine must be active in systemd." >&2; exit 1; }
docker_target="$(docker info --format '{{.OSType}}/{{.Architecture}}')"
[[ "$docker_target" == "linux/x86_64" || "$docker_target" == "linux/amd64" ]] || { echo "Linux/amd64 Docker Engine is required; detected $docker_target." >&2; exit 1; }

compose_path="${DOCKER_CONFIG:-$HOME/.docker}/cli-plugins/docker-compose"
compose_url="https://github.com/docker/compose/releases/download/v2.40.2/docker-compose-linux-x86_64"
compose_sha256="6c964d9655cd629ef43c5dc75d9612c2da319237debee54a7aef217e9f362b88"
if [[ -e "$compose_path" ]]; then
  actual="$(sha256sum "$compose_path" | awk '{print $1}')"
  [[ "$actual" == "$compose_sha256" ]] || { echo "Existing user Compose plugin has an unexpected checksum and was not overwritten." >&2; exit 1; }
else
  mkdir -p -- "$(dirname -- "$compose_path")"
  download="${compose_path}.download"
  trap 'rm -f -- "$download"' EXIT
  curl --fail --silent --show-error --location "$compose_url" --output "$download"
  actual="$(sha256sum "$download" | awk '{print $1}')"
  [[ "$actual" == "$compose_sha256" ]] || { echo "Downloaded Compose checksum does not match the pinned official checksum." >&2; exit 1; }
  chmod 0755 -- "$download"
  mv -- "$download" "$compose_path"
  trap - EXIT
fi
[[ "$(docker compose version --short)" == "2.40.2" ]] || { echo "Docker CLI did not select exact Compose 2.40.2." >&2; exit 1; }

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
environment_file="$repository_root/deploy/secrets/production.env"

read_required() {
  local prompt="$1" current="$2"
  if [[ -n "$current" ]]; then printf '%s' "$current"; return; fi
  local value
  read -r -p "$prompt: " value
  [[ -n "$value" ]] || { echo "$prompt is required." >&2; exit 1; }
  printf '%s' "$value"
}

if [[ ! -f "$environment_file" ]]; then
  app_image="$(read_required 'Immutable application image (name@sha256:...)' "$app_image")"
  edge_image="$(read_required 'Immutable edge image (name@sha256:...)' "$edge_image")"
  site_address="$(read_required 'Panel HTTPS address' "$site_address")"
  owner_email="$(read_required 'Initial Owner email' "$owner_email")"
  initialize_args=(
    --app-image "$app_image"
    --edge-image "$edge_image"
    --site-address "$site_address"
    --owner-email "$owner_email"
  )
  [[ -z "$owner_password_file" ]] || initialize_args+=(--owner-password-file "$owner_password_file")
  "$script_dir/initialize-deployment.sh" "${initialize_args[@]}"
else
  echo "Existing deploy/secrets/production.env was preserved; initialization was skipped."
fi

deploy_args=()
[[ "$deploy" == true ]] || deploy_args+=(--validate-only)
[[ "$bootstrap" == false ]] || deploy_args+=(--bootstrap)
"$script_dir/deploy.sh" "${deploy_args[@]}"
if [[ "$deploy" == false ]]; then
  echo "Preparation is complete. Run ./deploy/scripts/install-marketplacehub.sh --deploy --bootstrap for the first empty installation."
fi
