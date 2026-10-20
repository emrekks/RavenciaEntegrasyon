#!/usr/bin/env bash
set -Eeuo pipefail

# Monthly restore drill (ravencia-restore-drill.timer): restores the newest
# backup set into isolated, disposable containers with the running application
# image and checks schema, migrations, API and Worker health
# (deploy/backup/restore-drill.sh). Production containers and volumes are not touched.

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
sudo_cmd=(sudo -n)
# shellcheck source=/dev/null
source "$script_dir/lib.sh"
require_secret_files

backup_volume="marketplacehub_backup_staging"
postgres_image="$(sed -nE 's/^[[:space:]]*image: (postgres:[^[:space:]]+@sha256:[0-9a-f]{64})[[:space:]]*$/\1/p' "$repository_root/deploy/compose/compose.yaml" | head -n 1)"
[[ -n "$postgres_image" ]] || { echo "The pinned PostgreSQL image could not be read from compose.yaml." >&2; exit 1; }

api_container="$("${sudo_cmd[@]}" docker ps -q \
  --filter label=com.docker.compose.project=marketplacehub \
  --filter label=com.docker.compose.service=api | head -n 1)"
[[ -n "$api_container" ]] || { echo "The API container is not running; its image is the drill's application image." >&2; exit 1; }
app_image_id="$("${sudo_cmd[@]}" docker inspect --format '{{.Image}}' "$api_container")"

backup_set="$("${sudo_cmd[@]}" docker run --rm --network none -v "$backup_volume:/backup:ro" "$postgres_image" \
  sh -c 'cd /backup && ls -1d ????????T??????Z 2>/dev/null | sort | tail -n 1')"
[[ "$backup_set" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || { echo "No backup set was found in $backup_volume." >&2; exit 1; }

echo "Restore drill: backup set $backup_set, application image $app_image_id."
# The drill copies the root-owned secrets and drives Docker, so it runs under sudo.
"${sudo_cmd[@]}" bash "$repository_root/deploy/backup/restore-drill.sh" \
  "$backup_set" "$app_image_id" "$repository_root/deploy/secrets" "$backup_volume"
