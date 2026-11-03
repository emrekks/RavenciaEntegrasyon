#!/usr/bin/env bash
# Off-host copy of the newest verified backup set (security plan Y3), encrypted on the server to the owner's
# age PUBLIC key: the server can encrypt but never decrypt; the private key stays with the owner, off the host.
#
#   sudo bash deploy/backup/offsite-copy.sh
#
# Configuration, all under deploy/secrets/ (docs/CI-CD-DEPLOY.md "Sunucu dışı yedek"):
#   backup_age_recipient.txt   the owner's age public key(s), one "age1..." per line (age -R format)
#   backup_offsite_remote.txt  rclone destination "<remote>:<folder>", e.g. "gdrive:ravencia-yedek"
#   rclone.conf                rclone config holding that remote (the owner authorises it with `rclone config`)
# While the recipient file or the remote is missing the script prints "Off-site backup copy is not configured"
# and exits 0, so a timer can be installed before the owner has set it up. Once configured, a missing age or
# rclone binary, a failed upload or an empty uploaded object is an error (exit 1).
#
# Each set is uploaded once, as <folder>/<set>.tar.age (tar of the set directory, encrypted), streamed straight
# from the backup volume to the remote without a plaintext or ciphertext copy on disk. The script never deletes
# anything on the remote; retention there is the owner's rule. This does NOT protect the copies from a stolen
# server: rclone.conf holds a Drive token whose drive.file scope can delete or overwrite every file this app
# uploaded, so whoever controls the server can erase the off-site copies too. Only a destination the server
# cannot delete from (e.g. Backblaze B2 with Object Lock, or a separately owned copy) closes that gap.
# Restore (on the owner's computer): rclone copy <remote>:<folder>/<set>.tar.age . && age -d -i key.txt
# <set>.tar.age | tar -x, then deploy/backup/restore-drill.sh or docs/RUNBOOK-GERI-ALMA.md section 3.
#
# Test hooks: RAVENCIA_OFFSITE_SECRETS_DIR, RAVENCIA_OFFSITE_BACKUP_ROOT (instead of the Docker volume) and
# RAVENCIA_OFFSITE_LOCK.
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
secrets_dir="${RAVENCIA_OFFSITE_SECRETS_DIR:-$repository_root/deploy/secrets}"
recipient_file="$secrets_dir/backup_age_recipient.txt"
remote_file="$secrets_dir/backup_offsite_remote.txt"
rclone_config="$secrets_dir/rclone.conf"
backup_volume="${RAVENCIA_OFFSITE_BACKUP_VOLUME:-marketplacehub_backup_staging}"
lock_file="${RAVENCIA_OFFSITE_LOCK:-/run/lock/ravencia-offsite-copy.lock}"

not_configured() {
  echo "Off-site backup copy is not configured: $1 (docs/CI-CD-DEPLOY.md, \"Sunucu dışı yedek\"). Nothing was copied."
  exit 0
}

[[ -s "$recipient_file" ]] || not_configured "deploy/secrets/backup_age_recipient.txt is missing"
grep -qE '^age1[0-9a-z]+$' "$recipient_file" \
  || { echo "deploy/secrets/backup_age_recipient.txt holds no age public key (age1...); a private key never belongs on the server." >&2; exit 1; }
if grep -q 'AGE-SECRET-KEY' "$recipient_file"; then
  echo "deploy/secrets/backup_age_recipient.txt contains an age PRIVATE key: remove it from the server, keep only the public key." >&2
  exit 1
fi
[[ -s "$remote_file" ]] || not_configured "deploy/secrets/backup_offsite_remote.txt is missing"
destination="$(head -n 1 "$remote_file" | tr -d '[:space:]')"
[[ "$destination" =~ ^[A-Za-z0-9_-]+:[A-Za-z0-9._/-]*$ ]] \
  || { echo "deploy/secrets/backup_offsite_remote.txt must hold <remote>:<folder>, got '$destination'." >&2; exit 1; }
remote_name="${destination%%:*}"
folder="${destination#*:}"
folder="${folder%/}"
[[ -s "$rclone_config" ]] || not_configured "deploy/secrets/rclone.conf is missing"

for tool in age rclone tar sha256sum flock; do
  command -v "$tool" >/dev/null || { echo "Off-site copy is configured but '$tool' is not installed (apt-get install age rclone)." >&2; exit 1; }
done
export RCLONE_CONFIG="$rclone_config"
remotes="$(rclone listremotes)" || { echo "rclone could not read deploy/secrets/rclone.conf." >&2; exit 1; }
grep -qxF "$remote_name:" <<<"$remotes" || not_configured "rclone.conf has no remote named '$remote_name'"

# One copy at a time (a manual run next to the timer).
exec 9>>"$lock_file"
flock -n 9 || { echo "Another off-site copy is running; nothing to do."; exit 0; }

if [[ -n "${RAVENCIA_OFFSITE_BACKUP_ROOT:-}" ]]; then
  backup_root="$RAVENCIA_OFFSITE_BACKUP_ROOT"
else
  backup_root="$(docker volume inspect --format '{{.Mountpoint}}' "$backup_volume")"
fi
[[ -d "$backup_root" ]] || { echo "Backup volume directory '$backup_root' does not exist." >&2; exit 1; }

# Newest set whose files are all present and match their checksums (backup.sh also ran pg_restore --list on
# it when it was written). Set names are UTC timestamps, so the name order is the time order.
newest=""
while IFS= read -r candidate; do
  [[ -n "$candidate" ]] || continue
  dir="$backup_root/$candidate"
  if [[ -s "$dir/manifest.json" && -s "$dir/database.dump" && -s "$dir/private-volumes.tar.gz" ]] \
    && (cd "$dir" && sha256sum --check --quiet SHA256SUMS >/dev/null 2>&1); then
    newest="$candidate"
    break
  fi
  echo "Skipping backup set $candidate: incomplete or checksum mismatch." >&2
done < <(find "$backup_root" -mindepth 1 -maxdepth 1 -type d -regextype posix-extended -regex '.*/[0-9]{8}T[0-9]{6}Z' -printf '%f\n' | sort -r)
[[ -n "$newest" ]] || { echo "No verified backup set found in $backup_root." >&2; exit 1; }

object="$newest.tar.age"
target="$remote_name:${folder:+$folder/}$object"
# A folder that does not exist yet lists as an error: then nothing has been copied there.
existing="$(rclone lsf --files-only "$remote_name:$folder" 2>/dev/null)" || existing=""
if grep -qxF "$object" <<<"$existing"; then
  echo "Off-site copy of $newest already exists at $target; nothing to do."
  exit 0
fi

echo "Encrypting and uploading backup set $newest to $target."
tar -C "$backup_root" -cf - "$newest" | age --encrypt --recipients-file "$recipient_file" | rclone rcat "$target"
size="$(rclone lsf --files-only --format s "$target" 2>/dev/null)" || size=""
size="${size%%$'\n'*}"
if [[ ! "$size" =~ ^[0-9]+$ ]] || (( size == 0 )); then
  echo "Uploaded object $target is missing or empty." >&2
  exit 1
fi
echo "Off-site copy of $newest written to $target ($size bytes, age-encrypted)."
