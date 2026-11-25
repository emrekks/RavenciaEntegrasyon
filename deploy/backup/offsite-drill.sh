#!/usr/bin/env bash
# Drill for the encrypted Google Drive copy of the backups (scan report E-1). The worker uploads each copy with the owner's
# Google authorization, which lives encrypted in the database and is usable only by the app; the copy is encrypted to the
# owner's age PUBLIC key and the PRIVATE key is never on this server. So the drill has two halves:
#
#   offsite-drill.sh record
#       Scheduled, part of the monthly restore drill (monthly-restore-drill.sh), needs no key. Reads the upload record the
#       worker keeps (iam.tenant_settings "offsite-backup-state" of the host tenant, read-only) and fails unless the Drive
#       copy is switched on, its last upload is recent, nothing is failing, the authorization is valid, and the set it
#       sent still exists here and passes its checksums. It does NOT download or decrypt the copy and says so.
#
#   offsite-drill.sh restore COPY.tar.age AGE_IDENTITY_FILE
#       The owner's monthly manual step (docs/RUNBOOK-GERI-ALMA.md, "Drive kopyası tatbikatı"), on the server or on any
#       machine with Docker and this repository. COPY.tar.age is the newest copy downloaded from Drive; the identity file
#       holds the owner's age private key for this run only (put it on tmpfs, e.g. /dev/shm, and delete it afterwards: the
#       script only reads it, through a read-only bind mount, and never copies or stores it). Checks the age header and,
#       where the record is readable, the size and MD5 the worker recorded; decrypts inside the application image, checks
#       the archive's paths, unpacks it, verifies SHA256SUMS, compares them with this server's copy of the same set when
#       it still exists, and restores the set into throw-away containers with deploy/backup/restore-drill.sh (schemas,
#       migrations, API and Worker health). Everything it made (the decrypted files, the temporary volume, the drill's
#       containers) is removed on every path.
#
# Exit 0 only when every check passed; any failure exits non-zero (the restore-drill unit's OnFailure alert fires).
# Test hooks: RAVENCIA_OFFSITE_DRILL_RECORD (a record line instead of the database query), RAVENCIA_OFFSITE_DRILL_NOW
# (epoch seconds), RAVENCIA_OFFSITE_DRILL_BACKUP_ROOT (a directory instead of the backup volume),
# RAVENCIA_OFFSITE_DRILL_APP_IMAGE and RAVENCIA_OFFSITE_DRILL_RESTORE (the restore script to run).
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
backup_volume="marketplacehub_backup_staging"
# Two uploads a day (09:30 and 21:30 UTC): 36 hours means at least two slots were missed.
max_age_hours="${RAVENCIA_OFFSITE_MAX_AGE_HOURS:-36}"
set_pattern='^[0-9]{8}T[0-9]{6}Z$'

usage() {
  echo "usage: offsite-drill.sh record" >&2
  echo "       offsite-drill.sh restore COPY.tar.age AGE_IDENTITY_FILE" >&2
  exit 2
}

compose_container() {
  docker ps -q --filter label=com.docker.compose.project=marketplacehub --filter "label=com.docker.compose.service=$1" | head -n 1
}

# One line: enabled|lastSuccessEpoch|lastSet|lastSize|lastMd5|consecutiveFailures|authInvalidAt|remoteCopies|nowEpoch
# for the host tenant (the one the worker copies for). Read-only session; prints nothing when it cannot be read.
read_record() {
  local postgres_container
  if [[ -n "${RAVENCIA_OFFSITE_DRILL_RECORD:-}" ]]; then
    printf '%s\n' "$RAVENCIA_OFFSITE_DRILL_RECORD"
    return 0
  fi
  postgres_container="$(compose_container postgres)"
  if [[ -z "$postgres_container" ]]; then
    return 0
  fi
  docker exec -e PGOPTIONS='-c default_transaction_read_only=on' "$postgres_container" \
    psql -X -q -v ON_ERROR_STOP=1 -U marketplacehub -d marketplacehub -Atc "
      SELECT concat(coalesce(c.\"ValueJson\"::jsonb ->> 'enabled', 'false'), '|',
        coalesce(floor(extract(epoch FROM (st.\"ValueJson\"::jsonb ->> 'lastSuccessAt')::timestamptz))::bigint::text, ''), '|',
        coalesce(st.\"ValueJson\"::jsonb ->> 'lastSet', ''), '|', coalesce(st.\"ValueJson\"::jsonb ->> 'lastSize', ''), '|',
        coalesce(st.\"ValueJson\"::jsonb ->> 'lastMd5', ''), '|', coalesce(st.\"ValueJson\"::jsonb ->> 'consecutiveFailures', '0'), '|',
        coalesce(st.\"ValueJson\"::jsonb ->> 'authInvalidAt', ''), '|', coalesce(st.\"ValueJson\"::jsonb ->> 'remoteCopies', ''), '|',
        floor(extract(epoch FROM now()))::bigint)
      FROM iam.bootstrap_state b
      JOIN iam.tenants t ON t.\"Id\" = b.\"TenantId\" AND t.\"Status\" = 'ACTIVE'
      LEFT JOIN iam.tenant_settings c ON c.\"TenantId\" = b.\"TenantId\" AND c.\"Key\" = 'offsite-backup'
      LEFT JOIN iam.tenant_settings st ON st.\"TenantId\" = b.\"TenantId\" AND st.\"Key\" = 'offsite-backup-state'
      WHERE b.\"Key\" = 'initial-owner';" 2>/dev/null || true
}

backup_root() {
  if [[ -n "${RAVENCIA_OFFSITE_DRILL_BACKUP_ROOT:-}" ]]; then
    printf '%s\n' "$RAVENCIA_OFFSITE_DRILL_BACKUP_ROOT"
    return 0
  fi
  docker volume inspect --format '{{.Mountpoint}}' "$backup_volume" 2>/dev/null || true
}

run_record() {
  local record enabled success_epoch last_set last_size last_md5 failures auth_invalid copies now_epoch root age_seconds
  record="$(read_record | head -n 1)"
  [[ -n "$record" ]] || { echo "Off-site record check FAILED: the Drive copy's upload record could not be read (database or host tenant missing)." >&2; exit 1; }
  IFS='|' read -r enabled success_epoch last_set last_size last_md5 failures auth_invalid copies now_epoch <<<"$record"
  now_epoch="${RAVENCIA_OFFSITE_DRILL_NOW:-$now_epoch}"
  if [[ "$enabled" != true ]]; then
    echo "Off-site record check FAILED: the Google Drive copy is switched off (Sistem Ayarları › Google Drive Yedeği); no off-site copy is being made." >&2
    exit 1
  fi
  if [[ -n "$auth_invalid" ]]; then
    echo "Off-site record check FAILED: the Google authorization is invalid since $auth_invalid; reconnect it in the panel." >&2
    exit 1
  fi
  if [[ ! "$failures" =~ ^[0-9]+$ ]] || (( failures > 0 )); then
    echo "Off-site record check FAILED: the last $failures Drive copy runs failed (see the panel's Google Drive Yedeği card)." >&2
    exit 1
  fi
  if [[ ! "$success_epoch" =~ ^[0-9]+$ || ! "$now_epoch" =~ ^[0-9]+$ ]]; then
    echo "Off-site record check FAILED: no successful Drive copy is recorded." >&2
    exit 1
  fi
  age_seconds=$(( now_epoch - success_epoch ))
  if (( age_seconds > max_age_hours * 3600 )); then
    echo "Off-site record check FAILED: the last successful Drive copy is $(( age_seconds / 3600 )) hours old (limit ${max_age_hours})." >&2
    exit 1
  fi
  if [[ ! "$last_set" =~ $set_pattern ]]; then
    echo "Off-site record check FAILED: the recorded set name '$last_set' is not a backup set." >&2
    exit 1
  fi
  if [[ ! "$last_size" =~ ^[1-9][0-9]*$ || ! "$last_md5" =~ ^[0-9a-f]{32}$ ]]; then
    echo "Off-site record check FAILED: the record of $last_set has no size or MD5 from Drive (size '$last_size')." >&2
    exit 1
  fi
  root="$(backup_root)"
  if [[ -z "$root" || ! -d "$root/$last_set" ]]; then
    echo "Off-site record check FAILED: the set the last copy holds ($last_set) is not in the local backup volume." >&2
    exit 1
  fi
  if ! (cd "$root/$last_set" && sha256sum --check --quiet SHA256SUMS >/dev/null 2>&1); then
    echo "Off-site record check FAILED: the local files of $last_set no longer match their SHA256SUMS." >&2
    exit 1
  fi
  printf 'Off-site record check passed: set=%s sizeBytes=%s md5=%s copiedHoursAgo=%s driveCopies=%s localChecksums=ok. NOT tested: download from Drive and decryption (this host has neither the Google authorization nor the age private key); the owner runs "offsite-drill.sh restore" monthly (docs/RUNBOOK-GERI-ALMA.md).\n' \
    "$last_set" "$last_size" "$last_md5" "$(( age_seconds / 3600 ))" "${copies:-?}"
}

work_dir=""
drill_volume=""
cleanup() {
  if [[ -n "$drill_volume" ]]; then docker volume rm -f "$drill_volume" >/dev/null 2>&1 || true; fi
  if [[ -n "$work_dir" ]]; then rm -rf -- "$work_dir"; fi
}

run_restore() {
  local copy="$1" identity="$2" copy_name set_name header record last_set last_size last_md5
  local size md5 app_image api_container root member restore_script file
  [[ -s "$copy" ]] || { echo "Copy file '$copy' is missing or empty." >&2; exit 1; }
  [[ -s "$identity" ]] || { echo "Age identity file '$identity' is missing or empty." >&2; exit 1; }
  if ! grep -q '^AGE-SECRET-KEY-' "$identity"; then
    echo "Age identity file holds no age private key (AGE-SECRET-KEY-...)." >&2
    exit 1
  fi
  copy="$(cd -- "$(dirname -- "$copy")" && pwd -P)/${copy##*/}"
  identity="$(cd -- "$(dirname -- "$identity")" && pwd -P)/${identity##*/}"
  copy_name="${copy##*/}"
  set_name="${copy_name%.tar.age}"
  [[ "$copy_name" == "$set_name.tar.age" && "$set_name" =~ $set_pattern ]] \
    || { echo "Copy file must be named <set>.tar.age as on Drive (e.g. 20261125T213011Z.tar.age), got '$copy_name'." >&2; exit 1; }

  header="$(head -c 22 -- "$copy")"
  [[ "$header" == "age-encryption.org/v1" ]] || { echo "$copy_name is not an age file (first line '$header')." >&2; exit 1; }
  echo "Off-site restore drill: $copy_name has an age v1 header."

  size="$(stat -c %s -- "$copy")"
  md5="$(md5sum -- "$copy" | cut -d' ' -f1)"
  record="$(read_record | head -n 1)"
  if [[ -n "$record" ]]; then
    IFS='|' read -r _ _ last_set last_size last_md5 _ _ _ _ <<<"$record"
    if [[ "$last_set" == "$set_name" ]]; then
      if [[ "$last_size" != "$size" || "$last_md5" != "$md5" ]]; then
        echo "$copy_name does not match the worker's upload record (size $size, md5 $md5; recorded $last_size, $last_md5)." >&2
        exit 1
      fi
      echo "Off-site restore drill: size and MD5 match the worker's upload record ($size bytes)."
    else
      echo "Off-site restore drill: the record names set ${last_set:-none}, not $set_name; size and MD5 not compared."
    fi
  else
    echo "Off-site restore drill: no upload record readable here; size and MD5 not compared."
  fi

  if [[ -n "${RAVENCIA_OFFSITE_DRILL_APP_IMAGE:-}" ]]; then
    app_image="$RAVENCIA_OFFSITE_DRILL_APP_IMAGE"
  else
    api_container="$(compose_container api)"
    [[ -n "$api_container" ]] || { echo "The API container is not running; its image (which carries age) decrypts the copy. Set RAVENCIA_OFFSITE_DRILL_APP_IMAGE elsewhere." >&2; exit 1; }
    app_image="$(docker inspect --format '{{.Image}}' "$api_container")"
  fi

  trap cleanup EXIT
  trap 'exit 143' TERM INT
  umask 077
  work_dir="$(mktemp -d)"
  mkdir -p "$work_dir/extract"
  # The private key reaches only this container, read-only and without a network; it is never copied.
  docker run --rm -i --network none --user 0:0 --entrypoint age -v "$identity:/run/age-identity:ro" "$app_image" \
    --decrypt --identity /run/age-identity < "$copy" > "$work_dir/set.tar" \
    || { echo "Decrypting $copy_name failed: wrong key or a damaged copy." >&2; exit 1; }
  [[ -s "$work_dir/set.tar" ]] || { echo "Decrypting $copy_name produced nothing." >&2; exit 1; }
  echo "Off-site restore drill: decrypted."

  # Every member must sit under "<set>/": no absolute path, no "..".
  tar -tf "$work_dir/set.tar" > "$work_dir/members" || { echo "The decrypted archive is not a readable tar." >&2; exit 1; }
  while IFS= read -r member; do
    if [[ "$member" != "$set_name/"* || "$member" == *..* ]]; then
      echo "The archive holds an unexpected path: '$member'." >&2
      exit 1
    fi
  done < "$work_dir/members"
  tar -xf "$work_dir/set.tar" -C "$work_dir/extract"
  rm -f -- "$work_dir/set.tar"
  for file in manifest.json database.dump private-volumes.tar.gz SHA256SUMS; do
    [[ -s "$work_dir/extract/$set_name/$file" ]] || { echo "The copy of $set_name has no $file." >&2; exit 1; }
  done
  (cd "$work_dir/extract/$set_name" && sha256sum --check --quiet SHA256SUMS) \
    || { echo "The files in the copy of $set_name do not match its SHA256SUMS." >&2; exit 1; }
  echo "Off-site restore drill: unpacked, SHA256SUMS verified."

  root="$(backup_root)"
  if [[ -n "$root" && -s "$root/$set_name/SHA256SUMS" ]]; then
    cmp -s "$root/$set_name/SHA256SUMS" "$work_dir/extract/$set_name/SHA256SUMS" \
      || { echo "The copy's SHA256SUMS differ from this server's set $set_name." >&2; exit 1; }
    echo "Off-site restore drill: the copy holds exactly this server's set $set_name (SHA256SUMS identical)."
  else
    echo "Off-site restore drill: this machine has no local set $set_name to compare with; the restore below decides."
  fi

  drill_volume="marketplacehub_offsite_drill_$(printf '%s' "$set_name" | tr '[:upper:]' '[:lower:]')"
  if docker volume inspect "$drill_volume" >/dev/null 2>&1; then
    drill_volume=""
    echo "Volume marketplacehub_offsite_drill_* for $set_name already exists; another drill may be running." >&2
    exit 1
  fi
  docker volume create "$drill_volume" >/dev/null
  # The postgres image restore-drill.sh uses also copies the set into the throw-away volume.
  docker run --rm --network none -v "$work_dir/extract:/source:ro" -v "$drill_volume:/backup" \
    "postgres:18.6@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336" \
    cp -R "/source/$set_name" /backup/
  rm -rf -- "$work_dir/extract"

  restore_script="${RAVENCIA_OFFSITE_DRILL_RESTORE:-$script_dir/restore-drill.sh}"
  bash "$restore_script" "$set_name" "$app_image" "$repository_root/deploy/secrets" "$drill_volume"
  echo "Off-site restore drill passed: set=$set_name sizeBytes=$size md5=$md5 decrypted=yes checksums=ok restore=ok."
}

case "${1:-}" in
  record)
    [[ $# -eq 1 ]] || usage
    run_record
    ;;
  restore)
    [[ $# -eq 3 ]] || usage
    run_restore "$2" "$3"
    ;;
  *)
    usage
    ;;
esac
