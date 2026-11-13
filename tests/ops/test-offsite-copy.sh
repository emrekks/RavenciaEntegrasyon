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

# Stub age: marks the stream so the test can see it went through "encryption". FAKE_AGE_FAIL=1 dies mid-stream
# after writing part of it, as an age killed during the upload would.
cat > "$bin/age" <<'STUB'
#!/usr/bin/env bash
printf 'AGE-ENCRYPTED\n'
if [[ "${FAKE_AGE_FAIL:-}" == 1 ]]; then head -c 100; exit 1; fi
cat
STUB
# Stub rclone: one remote "gdrive" stored under $FAKE_REMOTE_DIR; every call is logged to $FAKE_RCLONE_LOG.
# FAKE_RCAT_TRUNCATE=1 stores a cut-off object (an upload that lost data); FAKE_HASH_UNSUPPORTED=1 has no
# backend SHA-256, so only `hashsum --download` answers.
cat > "$bin/rclone" <<'STUB'
#!/usr/bin/env bash
set -eu
echo "$*" >> "$FAKE_RCLONE_LOG"
path_of() { printf '%s/%s' "$FAKE_REMOTE_DIR" "${1#gdrive:}"; }
case "$1" in
  listremotes) echo "gdrive:" ;;
  rcat)
    path="$(path_of "$2")"; mkdir -p "$(dirname "$path")"
    if [[ "${FAKE_RCAT_TRUNCATE:-}" == 1 ]]; then head -c 20 > "$path"; cat > /dev/null; else cat > "$path"; fi ;;
  cat) cat "$(path_of "$2")" ;;
  moveto) mv -f "$(path_of "$2")" "$(path_of "$3")" ;;
  deletefile) rm -f "$(path_of "$2")" ;;
  hashsum)
    shift 2; download=false
    if [[ "$1" == --download ]]; then download=true; shift; fi
    path="$(path_of "$1")"
    [[ -f "$path" ]] || exit 3
    if [[ "${FAKE_HASH_UNSUPPORTED:-}" == 1 ]] && ! $download; then echo "hash type not supported" >&2; exit 1; fi
    echo "$(sha256sum < "$path" | cut -d ' ' -f 1)  $(basename "$path")" ;;
  lsf)
    shift; size=false; target=""
    while (($#)); do case "$1" in --files-only) shift ;; --format) [[ "$2" == s ]] && size=true; shift 2 ;; *) target="$1"; shift ;; esac; done
    path="$(path_of "$target")"
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
  PATH="$bin:$PATH" FAKE_REMOTE_DIR="$remote" FAKE_RCLONE_LOG="$work/rclone.log" RAVENCIA_OFFSITE_SECRETS_DIR="$secrets" \
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

folder="$remote/ravencia-yedek"
sidecar="$folder/20261102T085900Z.tar.age.sha256"
if [[ -s "$sidecar" ]] && [[ "$(cut -d ' ' -f 1 "$sidecar")" == "$(sha256sum < "$uploaded" | cut -d ' ' -f 1)" ]]; then
  pass "writes the object's SHA-256 to the sidecar"
else
  fail "writes the object's SHA-256 to the sidecar"
fi
assert_eq "20261102T085900Z.tar.age" "$(cut -d ' ' -f 3 "$sidecar")" "the sidecar is in sha256sum format"
assert_no_dir "$folder/20261102T085900Z.tar.age.partial" "leaves no partial object after a good upload"
assert_contains "$(cat "$work/rclone.log")" "rcat gdrive:ravencia-yedek/20261102T085900Z.tar.age.partial" "uploads under the partial name first"
assert_contains "$(cat "$work/rclone.log")" "moveto gdrive:ravencia-yedek/20261102T085900Z.tar.age.partial gdrive:ravencia-yedek/20261102T085900Z.tar.age" "renames the partial once verified"

status=0; output="$(run_copy)" || status=$?
assert_eq 0 "$status" "a verified copy is not an error"
assert_contains "$output" "already exists" "does not upload the same set twice"

# The copy on the remote changed after it was verified: uploaded again, and the run reports it.
echo "bit rot" >> "$uploaded"
status=0; output="$(run_copy)" || status=$?
assert_eq 2 "$status" "exits 2 when an existing copy does not match its sidecar"
assert_contains "$output" "does not match the sidecar" "says the copy did not match"
if tail -n +2 "$uploaded" | tar -tf - >/dev/null 2>&1 && [[ "$(cut -d ' ' -f 1 "$sidecar")" == "$(sha256sum < "$uploaded" | cut -d ' ' -f 1)" ]]; then
  pass "replaces the mismatching copy with a verified one"
else
  fail "replaces the mismatching copy with a verified one"
fi
status=0; output="$(run_copy)" || status=$?
assert_eq 0 "$status" "the replaced copy then counts as copied"

# A copy without a sidecar (an older run, or one stopped between rename and sidecar) is not trusted either.
rm -f "$sidecar"
status=0; output="$(run_copy)" || status=$?
assert_eq 2 "$status" "exits 2 when an existing copy has no sidecar"
assert_contains "$output" "has no SHA-256 sidecar" "says the sidecar is missing"
if [[ -s "$sidecar" ]]; then pass "writes the missing sidecar"; else fail "writes the missing sidecar"; fi

# A newer set; age dies mid-stream: no object under the real name, no partial left, an error.
make_set 20261104T085900Z
status=0; output="$(FAKE_AGE_FAIL=1 run_copy)" || status=$?
assert_eq 1 "$status" "fails when age dies mid-stream"
assert_no_dir "$folder/20261104T085900Z.tar.age" "a stream cut mid-way never becomes the object"
assert_no_dir "$folder/20261104T085900Z.tar.age.partial" "removes the partial after a failed stream"
assert_no_dir "$folder/20261104T085900Z.tar.age.sha256" "writes no sidecar after a failed stream"

# The upload lost data: the remote hash does not match the stream.
status=0; output="$(FAKE_RCAT_TRUNCATE=1 run_copy)" || status=$?
assert_eq 1 "$status" "fails when the uploaded object differs from the stream"
assert_contains "$output" "does not match the stream" "names the hash mismatch"
assert_no_dir "$folder/20261104T085900Z.tar.age" "a mismatching upload never becomes the object"
assert_no_dir "$folder/20261104T085900Z.tar.age.partial" "removes the mismatching partial"

# The next run after those failures uploads it; a backend without SHA-256 is hashed by downloading.
: > "$work/rclone.log"
status=0; output="$(FAKE_HASH_UNSUPPORTED=1 run_copy)" || status=$?
assert_eq 0 "$status" "uploads the set after earlier failures"
assert_contains "$(cat "$work/rclone.log")" "hashsum sha256 --download gdrive:ravencia-yedek/20261104T085900Z.tar.age.partial" "falls back to hashing a download"
if [[ -s "$folder/20261104T085900Z.tar.age.sha256" ]]; then pass "writes the sidecar with the download hash"; else fail "writes the sidecar with the download hash"; fi
status=0; output="$(FAKE_HASH_UNSUPPORTED=1 run_copy)" || status=$?
assert_eq 0 "$status" "verifies an existing copy by download when the backend has no SHA-256"
assert_contains "$output" "already exists" "the download hash also confirms an existing copy"

printf 'AGE-SECRET-KEY-1QQQQ\nage1qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqs3290gq\n' > "$secrets/backup_age_recipient.txt"
status=0; output="$(run_copy)" || status=$?
assert_eq 1 "$status" "refuses a recipient file holding a private key"

finish "off-site backup copy"
