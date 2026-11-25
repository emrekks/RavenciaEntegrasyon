#!/usr/bin/env bash
# deploy/backup/offsite-drill.sh: the scheduled record check and the owner's restore drill of a Drive copy, with a stub
# docker (no Docker, no keys, no network). The "encryption" is the age header line in front of a plain tar.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
script="$repository_root/deploy/backup/offsite-drill.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
backups="$work/backups"
volumes="$work/volumes"
bin="$work/bin"
mkdir -p "$backups" "$volumes" "$bin"
export FAKE_VOLUMES="$volumes" FAKE_LOG="$work/docker.log"

# Stub docker: age decrypts by dropping the header line; volumes are directories; the copy into the drill volume is a cp.
cat > "$bin/docker" <<'STUB'
#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" >> "$FAKE_LOG"
case "$1" in
  ps) exit 0 ;;
  volume)
    case "$2" in
      inspect) [[ -d "$FAKE_VOLUMES/$3" ]] ;;
      create) mkdir -p "$FAKE_VOLUMES/$3" ;;
      rm) rm -rf -- "${FAKE_VOLUMES:?}/${4:-$3}" ;;
      *) exit 2 ;;
    esac ;;
  run)
    if [[ "$*" == *"--entrypoint age"* ]]; then
      [[ "${FAKE_AGE_FAIL:-}" != 1 ]] || exit 1
      tail -n +2
      exit 0
    fi
    source="" target=""
    while (($#)); do
      case "$1" in
        -v) case "$2" in *:/source:ro) source="${2%%:/source:ro}" ;; *:/backup) target="$FAKE_VOLUMES/${2%%:/backup}" ;; esac; shift 2 ;;
        cp) cp -R "$source/${3#/source/}" "$target/"; exit 0 ;;
        *) shift ;;
      esac
    done
    exit 2 ;;
  *) exit 2 ;;
esac
STUB
# Stub restore-drill.sh: records what it got and checks the set reached the drill volume.
cat > "$work/restore-stub.sh" <<'STUB'
#!/usr/bin/env bash
set -eu
printf 'restore %s\n' "$*" >> "$FAKE_LOG"
test -s "$FAKE_VOLUMES/$4/$1/database.dump"
[[ "${FAKE_RESTORE_FAIL:-}" != 1 ]]
STUB
chmod +x "$bin/docker" "$work/restore-stub.sh"
export PATH="$bin:$PATH"

make_set() {
  local dir="$backups/$1"
  mkdir -p "$dir"
  echo '{}' > "$dir/manifest.json"
  echo "dump $1" > "$dir/database.dump"
  echo "files $1" > "$dir/private-volumes.tar.gz"
  (cd "$dir" && sha256sum manifest.json database.dump private-volumes.tar.gz > SHA256SUMS)
}
set_name=20261125T213011Z
make_set "$set_name"
now=1795643509
record_for() { printf 'true|%s|%s|%s|%s|0||26|%s' "$((now - 3600))" "$set_name" "$1" "$2" "$now"; }

run_record() {
  local status=0
  output="$(RAVENCIA_OFFSITE_DRILL_BACKUP_ROOT="$backups" RAVENCIA_OFFSITE_DRILL_RECORD="$1" bash "$script" record 2>&1)" || status=$?
  record_status="$status"
}

run_record "$(record_for 136958808 0123456789abcdef0123456789abcdef)"
assert_eq 0 "$record_status" "record: a recent, checked copy passes"
assert_contains "$output" "Off-site record check passed: set=$set_name" "record: names the set"
assert_contains "$output" "NOT tested: download from Drive and decryption" "record: says what it could not test"

run_record "false|$((now - 3600))|$set_name|1|0123456789abcdef0123456789abcdef|0||1|$now"
assert_eq 1 "$record_status" "record: Drive copy switched off fails"
run_record "true|$((now - 40 * 3600))|$set_name|1|0123456789abcdef0123456789abcdef|0||1|$now"
assert_eq 1 "$record_status" "record: a copy older than 36 hours fails"
assert_contains "$output" "40 hours old" "record: says how old"
run_record "true|$((now - 3600))|$set_name|1|0123456789abcdef0123456789abcdef|3||1|$now"
assert_eq 1 "$record_status" "record: failing runs fail"
run_record "true|$((now - 3600))|$set_name|1|0123456789abcdef0123456789abcdef|0|2026-11-20T10:00:00Z|1|$now"
assert_eq 1 "$record_status" "record: an invalid authorization fails"
run_record "true||||||||$now"
assert_eq 1 "$record_status" "record: no successful copy fails"
run_record "true|$((now - 3600))|20261101T000000Z|1|0123456789abcdef0123456789abcdef|0||1|$now"
assert_eq 1 "$record_status" "record: a set missing locally fails"
run_record "true|$((now - 3600))|$set_name||0123456789abcdef0123456789abcdef|0||1|$now"
assert_eq 1 "$record_status" "record: a record without a size fails"
echo "tampered" >> "$backups/$set_name/database.dump"
run_record "$(record_for 1 0123456789abcdef0123456789abcdef)"
assert_eq 1 "$record_status" "record: local checksum mismatch fails"
rm -rf -- "${backups:?}/$set_name"
make_set "$set_name"
status=0
output="$(RAVENCIA_OFFSITE_DRILL_BACKUP_ROOT="$backups" bash "$script" record 2>&1)" || status=$?
assert_eq 1 "$status" "record: an unreadable record (no database) fails"

# The owner's restore drill. The copy: age header + tar of "<set>/...".
copies="$work/copies"
mkdir -p "$copies"
make_copy() {
  local name="$1" source_root="$2"
  { printf 'age-encryption.org/v1\n'; tar -C "$source_root" -cf - "$set_name"; } > "$copies/$name"
}
make_copy "$set_name.tar.age" "$backups"
identity="$work/key.txt"
printf '# test\nAGE-SECRET-KEY-1TESTONLY\n' > "$identity"
size="$(stat -c %s "$copies/$set_name.tar.age")"
md5="$(md5sum "$copies/$set_name.tar.age" | cut -d' ' -f1)"

run_restore() {
  local copy="$1" record="${2:-}" status=0
  : > "$FAKE_LOG"
  output="$(RAVENCIA_OFFSITE_DRILL_BACKUP_ROOT="$backups" RAVENCIA_OFFSITE_DRILL_RECORD="$record" RAVENCIA_OFFSITE_DRILL_APP_IMAGE="sha256:$(printf '%064d' 1)" \
    RAVENCIA_OFFSITE_DRILL_RESTORE="$work/restore-stub.sh" TMPDIR="$work/tmp" bash "$script" restore "$copy" "$identity" 2>&1)" || status=$?
  restore_status="$status"
}
mkdir -p "$work/tmp"
leftovers() { find "$work/tmp" "$volumes" -mindepth 1 | wc -l | tr -d ' '; }

run_restore "$copies/$set_name.tar.age" "$(record_for "$size" "$md5")"
assert_eq 0 "$restore_status" "restore: a good copy passes"
assert_contains "$output" "size and MD5 match the worker's upload record" "restore: compared with the record"
assert_contains "$output" "SHA256SUMS identical" "restore: compared with the local set"
assert_contains "$output" "Off-site restore drill passed: set=$set_name" "restore: reports the pass"
assert_contains "$(cat "$FAKE_LOG")" "restore $set_name sha256:" "restore: restore-drill.sh ran on the set"
assert_contains "$(cat "$FAKE_LOG")" "marketplacehub_offsite_drill_20261125t213011z" "restore: into the throw-away volume"
assert_contains "$(cat "$FAKE_LOG")" "$identity:/run/age-identity:ro" "restore: the key is mounted read-only"
assert_eq 0 "$(leftovers)" "restore: nothing left behind after a pass"
assert_not_contains "$output" "TESTONLY" "restore: the key never reaches the output"

run_restore "$copies/$set_name.tar.age" "$(record_for 1 "$md5")"
assert_eq 1 "$restore_status" "restore: a size that differs from the record fails"
assert_eq 0 "$(leftovers)" "restore: nothing left after a record mismatch"

export FAKE_AGE_FAIL=1
run_restore "$copies/$set_name.tar.age"
unset FAKE_AGE_FAIL
assert_eq 1 "$restore_status" "restore: a failed decryption fails"
assert_contains "$output" "wrong key or a damaged copy" "restore: says why"
assert_eq 0 "$(leftovers)" "restore: nothing left after a failed decryption"

export FAKE_RESTORE_FAIL=1
run_restore "$copies/$set_name.tar.age"
unset FAKE_RESTORE_FAIL
assert_eq 1 "$restore_status" "restore: a failed restore fails"
assert_eq 0 "$(leftovers)" "restore: the drill volume goes after a failed restore"

printf 'not age\n' > "$copies/20261124T000000Z.tar.age"
run_restore "$copies/20261124T000000Z.tar.age"
assert_eq 1 "$restore_status" "restore: a file without the age header fails"

cp "$copies/$set_name.tar.age" "$copies/backup.tar.age"
run_restore "$copies/backup.tar.age"
assert_eq 1 "$restore_status" "restore: a copy not named <set>.tar.age fails"

# A copy whose files do not match its SHA256SUMS.
bad_root="$work/bad"
mkdir -p "$bad_root"
cp -R "$backups/$set_name" "$bad_root/"
echo "changed" >> "$bad_root/$set_name/database.dump"
make_copy "$set_name.tar.age" "$bad_root"
mkdir -p "$copies/bad"
mv "$copies/$set_name.tar.age" "$copies/bad/$set_name.tar.age"
run_restore "$copies/bad/$set_name.tar.age"
assert_eq 1 "$restore_status" "restore: a checksum mismatch in the copy fails"
assert_contains "$output" "do not match its SHA256SUMS" "restore: says which check failed"
assert_eq 0 "$(leftovers)" "restore: nothing left after a checksum mismatch"

# A copy with a path outside "<set>/".
evil_root="$work/evil"
mkdir -p "$evil_root/$set_name" "$evil_root/other"
echo x > "$evil_root/other/file"
{ printf 'age-encryption.org/v1\n'; tar -C "$evil_root" -cf - "$set_name" other; } > "$copies/evil.tmp"
mkdir -p "$copies/evil"
mv "$copies/evil.tmp" "$copies/evil/$set_name.tar.age"
run_restore "$copies/evil/$set_name.tar.age"
assert_eq 1 "$restore_status" "restore: a path outside the set fails"
assert_contains "$output" "unexpected path: 'other/" "restore: names the path"

printf 'no key here\n' > "$work/not-a-key.txt"
status=0
output="$(bash "$script" restore "$copies/evil/$set_name.tar.age" "$work/not-a-key.txt" 2>&1)" || status=$?
assert_eq 1 "$status" "restore: an identity without a private key is refused"

finish "offsite drill"
