#!/usr/bin/env bash
# Shared by secrets-escrow.sh and wal-archive.sh (VERITABANI-DAGITIM-8 and -9): host-made files that ride along in every
# off-site copy. Each part is published as <backup volume>/offsite-extra/<part>/ with *.age files, SHA256SUMS and a
# .verified marker written last; the worker's OffsiteBackupCopier puts a verified part into the Drive copy as
# <set>/extra/<part>/ and takes only files that are age-encrypted (BackupSetDirectory). Nothing plain is ever written there:
# every file is encrypted on the way in, to the owner's age PUBLIC key; the server cannot decrypt it.
#
# Test hooks: RAVENCIA_EXTRA_BACKUP_ROOT (a directory instead of the Docker volume), RAVENCIA_EXTRA_RECIPIENT (instead of
# the lookup), RAVENCIA_EXTRA_ENCRYPT (a command reading plain stdin and writing ciphertext; default: age in the worker).

extra_reader_gid=1654

if ! declare -p sudo_cmd >/dev/null 2>&1; then
  if (( EUID == 0 )); then sudo_cmd=(); else sudo_cmd=(sudo -n); fi
fi

extra_service_container() {
  "${sudo_cmd[@]}" docker ps -q --filter label=com.docker.compose.project=marketplacehub --filter "label=com.docker.compose.service=$1" | head -n 1
}

# The backup volume's directory on the host.
extra_backup_root() {
  if [[ -n "${RAVENCIA_EXTRA_BACKUP_ROOT:-}" ]]; then printf '%s' "$RAVENCIA_EXTRA_BACKUP_ROOT"; return; fi
  "${sudo_cmd[@]}" docker volume inspect --format '{{.Mountpoint}}' marketplacehub_backup_staging
}

# The owner's age public key: deploy/secrets/backup_age_recipient.txt when present, else the one the panel's Google Drive
# backup uses (iam.tenant_settings "offsite-backup", Sistem Ayarları › Google Drive Yedeği). Prints nothing when none.
extra_recipient() {
  local secrets_dir="$1" recipient=""
  if [[ -n "${RAVENCIA_EXTRA_RECIPIENT:-}" ]]; then recipient="$RAVENCIA_EXTRA_RECIPIENT"
  elif "${sudo_cmd[@]}" test -s "$secrets_dir/backup_age_recipient.txt"; then
    recipient="$("${sudo_cmd[@]}" head -n 1 "$secrets_dir/backup_age_recipient.txt" | tr -d '[:space:]')"
  else
    local postgres
    postgres="$(extra_service_container postgres)"
    if [[ -n "$postgres" ]]; then
      recipient="$("${sudo_cmd[@]}" docker exec "$postgres" psql -X -U marketplacehub -d marketplacehub -At -c \
        "select \"ValueJson\"::jsonb->>'ageRecipient' from iam.tenant_settings where \"Key\" = 'offsite-backup' order by \"UpdatedAt\" desc limit 1" 2>/dev/null || true)"
    fi
  fi
  if [[ "$recipient" =~ ^age1[0-9a-z]{58}$ ]]; then printf '%s' "$recipient"; fi
}

# Encrypts stdin to the recipient on stdout. The app image carries age (AgeCommandEncryptor); the running worker does it,
# so the host needs no age install.
extra_encrypt() {
  local recipient="$1" worker
  if [[ -n "${RAVENCIA_EXTRA_ENCRYPT:-}" ]]; then
    "$RAVENCIA_EXTRA_ENCRYPT" "$recipient"
    return
  fi
  worker="$(extra_service_container worker)"
  [[ -n "$worker" ]] || { echo "The worker container is not running; nothing can be encrypted." >&2; return 1; }
  "${sudo_cmd[@]}" docker exec -i "$worker" age --encrypt --recipient "$recipient"
}

# Publishes a staged part directory (holding only *.age files) as offsite-extra/<part>, replacing the previous one in
# one rename. The worker reads it as group 1654.
extra_publish() {
  local root="$1" part="$2" staged="$3" file
  for file in "$staged"/*; do
    [[ "$file" == *.age ]] || { echo "Refusing to publish ${file##*/}: only age-encrypted files go off the host." >&2; return 1; }
    [[ "$(head -c 22 -- "$file")" == "age-encryption.org/v1" ]] || { echo "Refusing to publish ${file##*/}: it is not age-encrypted." >&2; return 1; }
  done
  (cd -- "$staged" && sha256sum -- *.age > SHA256SUMS)
  "${sudo_cmd[@]}" install -d -m 0750 -g "$extra_reader_gid" -- "$root/offsite-extra"
  local target="$root/offsite-extra/$part" incoming="$root/offsite-extra/.$part.incoming-$$"
  "${sudo_cmd[@]}" rm -rf -- "$incoming"
  "${sudo_cmd[@]}" cp -R -- "$staged" "$incoming"
  # The marker goes in last, once every file is complete and readable by the worker.
  date -u +%Y-%m-%dT%H:%M:%SZ | "${sudo_cmd[@]}" tee "$incoming/.verified" >/dev/null
  "${sudo_cmd[@]}" chgrp -R "$extra_reader_gid" -- "$incoming"
  "${sudo_cmd[@]}" chmod 0750 -- "$incoming"
  "${sudo_cmd[@]}" find "$incoming" -type f -exec chmod 0640 {} +
  "${sudo_cmd[@]}" rm -rf -- "$target.old"
  if "${sudo_cmd[@]}" test -e "$target"; then "${sudo_cmd[@]}" mv -- "$target" "$target.old"; fi
  "${sudo_cmd[@]}" mv -- "$incoming" "$target"
  "${sudo_cmd[@]}" rm -rf -- "$target.old"
}
