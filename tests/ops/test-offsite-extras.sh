#!/usr/bin/env bash
# deploy/backup/secrets-escrow.sh and wal-archive.sh ship (VERITABANI-DAGITIM-8/-9) against a stub encryptor and a plain
# directory as the backup volume: what gets published in offsite-extra, when the escrow counts as stale, and that a plain
# file is never published.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

echo "off-site extras"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/secrets" "$work/backup" "$work/state" "$work/wal/base/20261116T030000Z" "$work/wal/wal"
# sudo runs the command; owner, group and mode changes are dropped (no group 1654 here, and not every test file system
# takes modes).
cat > "$work/bin/sudo" <<'EOF'
#!/usr/bin/env bash
[[ "${1:-}" == -n ]] && shift
case "$1" in
  chgrp|chmod) exit 0 ;;
  install)
    shift; args=()
    while (($#)); do case "$1" in -o|-g|-m) shift 2 ;; *) args+=("$1"); shift ;; esac; done
    exec install "${args[@]}" ;;
esac
exec "$@"
EOF
# A stand-in for age: the real header, then the plain stream (the tests read it back).
cat > "$work/bin/fake-age" <<'EOF'
#!/usr/bin/env bash
printf 'age-encryption.org/v1\n'
cat
EOF
cat > "$work/bin/plain-age" <<'EOF'
#!/usr/bin/env bash
cat
EOF
chmod +x "$work/bin/"*
for name in credential_key.txt dp_certificate.pfx dp_certificate_password.txt production.env; do printf 'secret %s\n' "$name" > "$work/secrets/$name"; done

recipient="age1$(printf 'q%.0s' {1..58})"
run() {
  local script="$1"; shift
  PATH="$work/bin:$PATH" RAVENCIA_EXTRA_BACKUP_ROOT="$work/backup" RAVENCIA_EXTRA_RECIPIENT="${RECIPIENT-$recipient}" \
    RAVENCIA_EXTRA_ENCRYPT="${ENCRYPT:-$work/bin/fake-age}" RAVENCIA_ESCROW_SECRETS_DIR="$work/secrets" \
    RAVENCIA_ESCROW_STATE_DIR="$work/state" RAVENCIA_WAL_ARCHIVE_ROOT="$work/wal" \
    bash "$repository_root/deploy/backup/$script" "$@" 2>&1
}
escrow_part="$work/backup/offsite-extra/escrow"

status=0; output="$(run secrets-escrow.sh --check)" || status=$?
assert_eq "3" "$status" "a missing escrow is reported ($output)"
status=0; output="$(run secrets-escrow.sh)" || status=$?
assert_eq "0" "$status" "the escrow is built ($output)"
assert_eq "SHA256SUMS ravencia-secrets.tar.age" "$(find "$escrow_part" -mindepth 1 -maxdepth 1 -not -name '.*' -printf '%f\n' | sort | tr '\n' ' ' | sed 's/ $//')" "only the encrypted file is published"
if [[ -f "$escrow_part/.verified" && -f "$escrow_part/SHA256SUMS" ]]; then pass "with its checksums and marker"; else fail "with its checksums and marker"; fi
if (cd "$escrow_part" && sha256sum -c --quiet SHA256SUMS); then pass "the checksums match"; else fail "the checksums match"; fi
names="$(tail -c +23 "$escrow_part/ravencia-secrets.tar.age" | tar -tf - | sort | tr '\n' ' ')"
assert_eq "credential_key.txt dp_certificate.pfx dp_certificate_password.txt production.env " "$names" "the escrow holds the four files a new server needs"
status=0; output="$(run secrets-escrow.sh --check)" || status=$?
assert_eq "0" "$status" "a fresh escrow passes the check ($output)"

printf 'rotated\n' > "$work/secrets/dp_certificate_password.txt"
status=0; output="$(run secrets-escrow.sh --check)" || status=$?
assert_eq "3" "$status" "a changed secret makes the escrow stale"
assert_contains "$output" "older than the current secrets" "and says why"
status=0; output="$(RECIPIENT="age1$(printf 'z%.0s' {1..58})" run secrets-escrow.sh --check)" || status=$?
assert_eq "3" "$status" "a changed age key makes the escrow stale"
status=0; output="$(run secrets-escrow.sh)" || status=$?
assert_eq "0" "$status" "a refresh builds it again"
assert_contains "$(tail -c +23 "$escrow_part/ravencia-secrets.tar.age" | tar -xOf - dp_certificate_password.txt)" "rotated" "with the new secret"

status=0; output="$(RECIPIENT="" run secrets-escrow.sh)" || status=$?
assert_eq "1" "$status" "no age key, no escrow"
before="$(cat "$escrow_part/SHA256SUMS")"
printf 'rotated again\n' > "$work/secrets/credential_key.txt"
status=0; output="$(ENCRYPT="$work/bin/plain-age" run secrets-escrow.sh)" || status=$?
assert_eq "1" "$status" "an encryptor that leaves the stream plain is refused"
assert_contains "$output" "not age-encrypted" "and says so"
assert_eq "$before" "$(cat "$escrow_part/SHA256SUMS")" "the published escrow is left as it was"

# WAL: a base backup starting at segment ...05; older segments are not shipped, newer ones and history files are.
mkdir -p "$work/label"
echo 'START WAL LOCATION: 0/5000028 (file 000000010000000000000005)' > "$work/label/backup_label"
tar -C "$work/label" -czf "$work/wal/base/20261116T030000Z/base.tar.gz" backup_label
for segment in 03 04 05 06 07; do printf 'wal %s' "$segment" | gzip > "$work/wal/wal/0000000100000000000000$segment.gz"; done
printf 'backup' | gzip > "$work/wal/wal/000000010000000000000005.00000028.backup.gz"
printf 'history' | gzip > "$work/wal/wal/00000002.history.gz"
status=0; output="$(run wal-archive.sh ship)" || status=$?
assert_eq "0" "$status" "the WAL archive is shipped ($output)"
shipped="$(tail -c +23 "$work/backup/offsite-extra/wal/wal-archive.tar.age" | tar -tf - | grep -v '/$' | sort | tr '\n' ' ')"
assert_eq "base/20261116T030000Z/base.tar.gz wal/000000010000000000000005.00000028.backup.gz wal/000000010000000000000005.gz wal/000000010000000000000006.gz wal/000000010000000000000007.gz wal/00000002.history.gz " "$shipped" "base backup and the WAL from its start on"
if [[ -f "$escrow_part/.verified" ]]; then pass "the escrow part is untouched by the WAL part"; else fail "the escrow part is untouched by the WAL part"; fi

finish "off-site extras"
