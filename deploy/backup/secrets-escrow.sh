#!/usr/bin/env bash
# Secrets escrow (VERITABANI-DAGITIM-9, docs/RUNBOOK-GERI-ALMA.md section 5). A backup set holds the database and the
# key ring, but not the certificate that opens the key ring, nor the credential key: without them a restore after the
# server is lost cannot read any marketplace credential. This script keeps an age-encrypted copy of those files in every
# off-site copy (offsite-extra-lib.sh: <set>/extra/escrow/ravencia-secrets.tar.age), encrypted to the owner's age public
# key (the panel's Google Drive backup key, or deploy/secrets/backup_age_recipient.txt). Never plain; the server cannot
# decrypt it.
#
#   sudo bash deploy/backup/secrets-escrow.sh            refresh: build a new escrow when none exists, a secret changed
#                                                        or the key changed; otherwise nothing (exit 0)
#   sudo bash deploy/backup/secrets-escrow.sh --check    report only: exit 0 fresh, 3 missing or stale (prints why)
#
# ravencia-secrets-escrow.timer runs the refresh daily once the owner switched it on (install-backup-timer.sh, flag file
# deploy/secrets/secrets_escrow.enabled); a failed refresh alerts through OnFailure. The check is how a stale escrow is
# seen: the escrow records a fingerprint of the files and the key it was made from (kept root-only beside it, never sent).
# Test hooks: RAVENCIA_ESCROW_SECRETS_DIR, RAVENCIA_ESCROW_STATE_DIR and those of offsite-extra-lib.sh.
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
secrets_dir="${RAVENCIA_ESCROW_SECRETS_DIR:-$repository_root/deploy/secrets}"
# shellcheck source=deploy/backup/offsite-extra-lib.sh
source "$script_dir/offsite-extra-lib.sh"

mode="${1:-refresh}"
case "$mode" in refresh|--check) ;; *) echo "usage: secrets-escrow.sh [--check]" >&2; exit 2 ;; esac

# What a new server needs that no backup set holds. postgres_password.txt and the database connections are not needed:
# a restore creates a fresh database password (restore-drill.sh --escrow does the same).
escrow_files=(credential_key.txt dp_certificate.pfx dp_certificate_password.txt production.env)

backup_root="$(extra_backup_root)"
state_dir="${RAVENCIA_ESCROW_STATE_DIR:-$backup_root/escrow-state}"
recipient="$(extra_recipient "$secrets_dir")"

stale() {
  echo "Secrets escrow is $1."
  if [[ "$mode" == --check ]]; then exit 3; fi
}

[[ -n "$recipient" ]] || { echo "Secrets escrow cannot be made: no age public key (panel: Sistem Ayarları › Google Drive Yedeği, or deploy/secrets/backup_age_recipient.txt)." >&2; exit 1; }
for name in "${escrow_files[@]}"; do
  "${sudo_cmd[@]}" test -s "$secrets_dir/$name" || { echo "Secrets escrow cannot be made: deploy/secrets/$name is missing." >&2; exit 1; }
done

fingerprint="$( { printf 'recipient %s\n' "$recipient"; for name in "${escrow_files[@]}"; do printf '%s %s\n' "$name" "$("${sudo_cmd[@]}" sha256sum -- "$secrets_dir/$name" | cut -d' ' -f1)"; done; } | sha256sum | cut -d' ' -f1)"
recorded="$("${sudo_cmd[@]}" cat -- "$state_dir/fingerprint" 2>/dev/null || true)"
published="$backup_root/offsite-extra/escrow/.verified"

if [[ "$recorded" == "$fingerprint" ]] && "${sudo_cmd[@]}" test -f "$published"; then
  echo "Secrets escrow is fresh (made $("${sudo_cmd[@]}" cat -- "$published"))."
  exit 0
fi
if ! "${sudo_cmd[@]}" test -f "$published"; then stale "missing"
elif [[ -z "$recorded" ]]; then stale "of unknown age (no fingerprint)"
else stale "older than the current secrets or age key"; fi

staged="$(mktemp -d)"
trap 'rm -rf -- "$staged"' EXIT
chmod 0700 "$staged"
# The tar streams straight into age: no plain copy is written anywhere.
"${sudo_cmd[@]}" tar -C "$secrets_dir" -cf - -- "${escrow_files[@]}" | extra_encrypt "$recipient" > "$staged/ravencia-secrets.tar.age"
[[ -s "$staged/ravencia-secrets.tar.age" ]] || { echo "Encryption produced nothing." >&2; exit 1; }
extra_publish "$backup_root" escrow "$staged"
"${sudo_cmd[@]}" install -d -m 0700 -- "$state_dir"
printf '%s' "$fingerprint" | "${sudo_cmd[@]}" tee "$state_dir/fingerprint" >/dev/null
echo "Secrets escrow refreshed: offsite-extra/escrow/ravencia-secrets.tar.age goes with the next off-site copy."
