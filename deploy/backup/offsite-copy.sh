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
# rclone binary, a failed upload or an uploaded object whose SHA-256 differs from the stream is an error (exit 1).
#
# Each set is uploaded once, as <folder>/<set>.tar.age (tar of the set directory, encrypted), streamed straight
# from the backup volume to the remote without a plaintext or ciphertext copy on disk. The stream goes up as
# <set>.tar.age.partial while its SHA-256 is taken on the way; only when the remote's SHA-256 of that object equals
# the stream's is it renamed to <set>.tar.age and the sidecar <set>.tar.age.sha256 (sha256sum format) written. A set
# counts as copied only when the object and its sidecar exist and the object's remote SHA-256 still matches the
# sidecar; a copy without a matching sidecar is uploaded again and the run then exits 2, so the unit's OnFailure=
# alert reports it. A tar, age or upload that dies mid-stream leaves no <set>.tar.age (the partial is removed).
# Backends or rclone versions without a SHA-256 for the object are hashed with `rclone hashsum --download`.
# Apart from its own partial the script never deletes anything on the remote; retention there is the owner's rule. This does NOT protect the copies from a stolen
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

for tool in age rclone tar sha256sum flock tee mkfifo; do
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
prefix="$remote_name:${folder:+$folder/}"
target="$prefix$object"
partial="$target.partial"
sidecar="$target.sha256"

# SHA-256 of a remote object: the backend's own when it has one, else rclone downloads and hashes it.
remote_sha256() {
  local out hash
  out="$(rclone hashsum sha256 "$1" 2>/dev/null)" || out=""
  hash="${out%%[[:space:]]*}"
  if [[ ! "$hash" =~ ^[0-9a-f]{64}$ ]]; then
    out="$(rclone hashsum sha256 --download "$1" 2>/dev/null)" || out=""
    hash="${out%%[[:space:]]*}"
  fi
  [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || return 1
  printf '%s\n' "$hash"
}

# A folder that does not exist yet lists as an error: then nothing has been copied there.
existing="$(rclone lsf --files-only "$remote_name:$folder" 2>/dev/null)" || existing=""
recopy=""
if grep -qxF "$object" <<<"$existing"; then
  recorded=""
  if grep -qxF "$object.sha256" <<<"$existing"; then
    recorded="$(rclone cat "$sidecar" 2>/dev/null | head -n 1)" || recorded=""
    recorded="${recorded%%[[:space:]]*}"
  fi
  actual="$(remote_sha256 "$target")" || actual=""
  if [[ "$recorded" =~ ^[0-9a-f]{64}$ && "$actual" == "$recorded" ]]; then
    echo "Off-site copy of $newest already exists at $target and matches its SHA-256; nothing to do."
    exit 0
  fi
  if [[ -z "$recorded" ]]; then
    recopy="it has no SHA-256 sidecar ($object.sha256)"
  else
    recopy="its SHA-256 (${actual:-unreadable}) does not match the sidecar ($recorded)"
  fi
  echo "WARNING: the off-site copy $target is not verified: $recopy. Uploading it again." >&2
fi

work_dir="$(mktemp -d)"
uploaded=""
cleanup() {
  rm -rf -- "$work_dir"
  # A failed run leaves no partial object behind (best effort; the next run overwrites it anyway).
  [[ -n "$uploaded" ]] || rclone deletefile "$partial" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Encrypting and uploading backup set $newest to $partial."
stream_sum="$work_dir/stream.sha256"
# tee copies the encrypted stream into a FIFO that sha256sum reads in the background; this shell waits for it.
mkfifo "$work_dir/stream"
sha256sum <"$work_dir/stream" >"$stream_sum" &
hasher=$!
upload_status=0
tar -C "$backup_root" -cf - "$newest" \
  | age --encrypt --recipients-file "$recipient_file" \
  | tee "$work_dir/stream" \
  | rclone rcat "$partial" || upload_status=$?
wait "$hasher" || { (( upload_status )) || upload_status=1; }
if (( upload_status != 0 )); then
  echo "Encrypting or uploading $newest failed (status $upload_status); no copy was written to $target." >&2
  exit 1
fi
expected="$(cut -d ' ' -f 1 "$stream_sum" 2>/dev/null)" || expected=""
[[ "$expected" =~ ^[0-9a-f]{64}$ ]] || { echo "Could not read the SHA-256 of the uploaded stream." >&2; exit 1; }
actual="$(remote_sha256 "$partial")" || { echo "Could not read the SHA-256 of $partial on the remote." >&2; exit 1; }
if [[ "$actual" != "$expected" ]]; then
  echo "Uploaded object $partial does not match the stream (SHA-256 $actual, expected $expected); it was removed." >&2
  exit 1
fi
rclone moveto "$partial" "$target" || { echo "Could not rename $partial to $target." >&2; exit 1; }
uploaded=yes
printf '%s  %s\n' "$expected" "$object" | rclone rcat "$sidecar" \
  || { echo "Could not write $sidecar; the next run will upload $newest again." >&2; exit 1; }
size="$(rclone lsf --files-only --format s "$target" 2>/dev/null)" || size=""
size="${size%%$'\n'*}"
echo "Off-site copy of $newest written to $target (${size:-?} bytes, age-encrypted, SHA-256 $expected)."
if [[ -n "$recopy" ]]; then
  echo "The earlier copy of $newest was replaced because $recopy; exiting 2 so the failure alert reports it." >&2
  exit 2
fi
