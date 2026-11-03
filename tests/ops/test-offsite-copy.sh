#!/usr/bin/env bash
# deploy/backup/offsite-copy.sh with stub age and rclone binaries (no network, no keys).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
script="$repository_root/deploy/backup/offsite-copy.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
secrets="$work/secrets"
backups="$work/backups"
remote="$work/remote"
bin="$work/bin"
mkdir -p "$secrets" "$backups" "$remote" "$bin"

# Stub age: marks the stream so the test can see it went through "encryption".
cat > "$bin/age" <<'STUB'
#!/usr/bin/env bash
printf 'AGE-ENCRYPTED\n'; cat
STUB
# Stub rclone: one remote "gdrive" stored under $FAKE_REMOTE_DIR.
cat > "$bin/rclone" <<'STUB'
#!/usr/bin/env bash
set -eu
case "$1" in
  listremotes) echo "gdrive:" ;;
  rcat) path="$FAKE_REMOTE_DIR/${2#gdrive:}"; mkdir -p "$(dirname "$path")"; cat > "$path" ;;
  lsf)
    shift; size=false; target=""
    while (($#)); do case "$1" in --files-only) shift ;; --format) [[ "$2" == s ]] && size=true; shift 2 ;; *) target="$1"; shift ;; esac; done
    path="$FAKE_REMOTE_DIR/${target#gdrive:}"
    if [[ -f "$path" ]]; then if $size; then stat -c %s "$path"; else basename "$path"; fi
    elif [[ -d "$path" ]]; then find "$path" -maxdepth 1 -type f -printf '%f\n'
    else exit 3; fi ;;
  *) exit 2 ;;
esac
STUB
chmod +x "$bin/age" "$bin/rclone"
# Git Bash on Windows has no flock; the server and CI do.
if ! command -v flock >/dev/null; then printf '#!/usr/bin/env bash\nexit 0\n' > "$bin/flock"; chmod +x "$bin/flock"; fi

make_set() {
  local dir="$backups/$1"
  mkdir -p "$dir"
  echo '{}' > "$dir/manifest.json"
  echo "dump $1" > "$dir/database.dump"
  echo "files $1" > "$dir/private-volumes.tar.gz"
  (cd "$dir" && sha256sum database.dump private-volumes.tar.gz > SHA256SUMS)
}

run_copy() {
  PATH="$bin:$PATH" FAKE_REMOTE_DIR="$remote" RAVENCIA_OFFSITE_SECRETS_DIR="$secrets" \
    RAVENCIA_OFFSITE_BACKUP_ROOT="$backups" RAVENCIA_OFFSITE_LOCK="$work/lock" bash "$script" 2>&1
}

echo "off-site backup copy"

status=0; output="$(run_copy)" || status=$?
assert_eq 0 "$status" "exits 0 without a recipient file"
assert_contains "$output" "not configured" "says it is not configured without a recipient file"

echo "age1qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqs3290gq" > "$secrets/backup_age_recipient.txt"
status=0; output="$(run_copy)" || status=$?
assert_eq 0 "$status" "exits 0 without a remote"
assert_contains "$output" "backup_offsite_remote.txt is missing" "names the missing remote file"

echo "gdrive:ravencia-yedek" > "$secrets/backup_offsite_remote.txt"
status=0; output="$(run_copy)" || status=$?
assert_eq 0 "$status" "exits 0 without rclone.conf"
assert_contains "$output" "rclone.conf is missing" "names the missing rclone config"

echo "[gdrive]" > "$secrets/rclone.conf"
make_set 20261101T085900Z
make_set 20261102T085900Z
make_set 20261103T085900Z
echo "corrupted" >> "$backups/20261103T085900Z/database.dump"
status=0; output="$(run_copy)" || status=$?
assert_eq 0 "$status" "uploads when configured"
assert_contains "$output" "Skipping backup set 20261103T085900Z" "skips a set whose checksum does not match"
assert_contains "$output" "20261102T085900Z written to gdrive:ravencia-yedek/20261102T085900Z.tar.age" "uploads the newest verified set"
uploaded="$remote/ravencia-yedek/20261102T085900Z.tar.age"
if [[ -s "$uploaded" ]] && head -n 1 "$uploaded" | grep -qx 'AGE-ENCRYPTED'; then
  pass "the uploaded object went through age"
else
  fail "the uploaded object went through age"
fi
if tail -n +2 "$uploaded" | tar -tf - | grep -qx '20261102T085900Z/database.dump'; then
  pass "the object is a tar of the set directory"
else
  fail "the object is a tar of the set directory"
fi

output="$(run_copy)"
assert_contains "$output" "already exists" "does not upload the same set twice"

printf 'AGE-SECRET-KEY-1QQQQ\nage1qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqs3290gq\n' > "$secrets/backup_age_recipient.txt"
status=0; output="$(run_copy)" || status=$?
assert_eq 1 "$status" "refuses a recipient file holding a private key"

finish "off-site backup copy"
