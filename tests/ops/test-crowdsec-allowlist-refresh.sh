#!/usr/bin/env bash
# deploy/scripts/crowdsec-allowlist-refresh.sh with stub curl, jq, cscli, crowdsec and systemctl (no root, no network):
# GitHub Actions ranges become an SSH-only postoverflow whitelist, written only after "crowdsec -t" and a reload accept it,
# and the old every-scenario allowlist goes only then (scan report A-11).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
script="$repository_root/deploy/scripts/crowdsec-allowlist-refresh.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
bin="$work/bin"
lists="$work/lists"
postoverflows="$work/postoverflows"
mkdir -p "$bin" "$lists" "$postoverflows"
export FAKE_LISTS="$lists" FAKE_LOG="$work/calls.log" FAKE_RANGES="$work/ranges"

# The stubs pass plain lines where the real tools pass JSON: the fake "meta.json" and "inspect" outputs are one value
# per line and the stub jq prints them as they are.
cat > "$bin/curl" <<'STUB'
#!/usr/bin/env bash
set -eu
out=""
while (($#)); do if [[ "$1" == -o ]]; then out="$2"; shift 2; else shift; fi; done
[[ "${FAKE_CURL_FAIL:-}" != 1 ]] || exit 22
cp "$FAKE_RANGES" "$out"
STUB
cat > "$bin/jq" <<'STUB'
#!/usr/bin/env bash
set -eu
if [[ "$1" == -r ]]; then shift 2; fi
if (($#)); then cat "$1"; else cat; fi
STUB
cat > "$bin/cscli" <<'STUB'
#!/usr/bin/env bash
set -eu
printf 'cscli %s\n' "$*" >> "$FAKE_LOG"
[[ "$1" == allowlists ]] || exit 2
name="${3:-}"
case "$2" in
  inspect) [[ -f "$FAKE_LISTS/$name" ]] || exit 1; cat "$FAKE_LISTS/$name" ;;
  create) : > "$FAKE_LISTS/$name" ;;
  delete) rm -f -- "$FAKE_LISTS/$name" ;;
  add) shift 3; while (($#)); do [[ "$1" == -d ]] && break; printf '%s\n' "$1" >> "$FAKE_LISTS/$name"; shift; done ;;
  remove) shift 3; for value in "$@"; do grep -vxF -- "$value" "$FAKE_LISTS/$name" > "$FAKE_LISTS/$name.new" || true; mv "$FAKE_LISTS/$name.new" "$FAKE_LISTS/$name"; done ;;
  *) exit 2 ;;
esac
STUB
cat > "$bin/crowdsec" <<'STUB'
#!/usr/bin/env bash
printf 'crowdsec %s\n' "$*" >> "$FAKE_LOG"
[[ "${FAKE_CONFIG_TEST_FAIL:-}" != 1 ]]
STUB
cat > "$bin/systemctl" <<'STUB'
#!/usr/bin/env bash
printf 'systemctl %s\n' "$*" >> "$FAKE_LOG"
[[ "${FAKE_RELOAD_FAIL:-}" != 1 ]]
STUB
chmod +x "$bin"/*
export PATH="$bin:$PATH"

for i in $(seq 1 1200); do printf '10.%d.%d.0/24\n' "$((i / 256))" "$((i % 256))"; done > "$FAKE_RANGES"
printf '2001:db8::/48\n' >> "$FAKE_RANGES"
whitelist="$postoverflows/ravencia-github-actions-ssh.yaml"

marker="$work/crowdsec-ssh-only-allowlist"

run_refresh() {
  local status=0
  : > "$FAKE_LOG"
  output="$(RAVENCIA_CROWDSEC_SKIP_ROOT_CHECK=1 RAVENCIA_CROWDSEC_POSTOVERFLOW_DIR="$postoverflows" RAVENCIA_CROWDSEC_SSH_ONLY_MARKER="$marker" bash "$script" 2>&1)" || status=$?
  refresh_status="$status"
}

# No owner marker (a deploy installed the new script and unit): the old arrangement stays exactly as it was.
printf '10.0.1.0/24\n' > "$lists/github-actions"
run_refresh
assert_eq 0 "$refresh_status" "without the marker the run passes"
assert_contains "$output" "the old arrangement stays" "says the old arrangement stays"
assert_no_dir "$whitelist" "without the marker no postoverflow file is written"
assert_eq 1201 "$(wc -l < "$lists/github-actions" | tr -d ' ')" "without the marker the old allowlist is kept current"
assert_not_contains "$(cat "$FAKE_LOG")" "allowlists delete" "without the marker nothing is deleted"
assert_not_contains "$(cat "$FAKE_LOG")" "systemctl reload" "without the marker CrowdSec is not reloaded"
assert_eq 0 "$(find "$postoverflows" -mindepth 1 | wc -l | tr -d ' ')" "without the marker the postoverflow directory is untouched"

# The owner's switch (install-crowdsec.sh writes the marker).
: > "$marker"
printf '10.0.1.0/24\n' > "$lists/github-actions"
run_refresh
assert_eq 0 "$refresh_status" "first run passes"
assert_contains "$(cat "$whitelist")" "filter: \"evt.Overflow.Alert.GetScenario() startsWith 'crowdsecurity/ssh-'\"" "the whitelist covers the SSH scenarios only"
assert_contains "$(cat "$whitelist")" '    - "10.4.176.0/24"' "the whitelist lists the ranges"
assert_contains "$(cat "$whitelist")" '    - "2001:db8::/48"' "IPv6 ranges too"
assert_eq 1201 "$(grep -c '^    - "' "$whitelist")" "every range once"
assert_contains "$(cat "$FAKE_LOG")" "crowdsec -t" "the configuration was tested"
assert_contains "$(cat "$FAKE_LOG")" "systemctl reload crowdsec" "CrowdSec reloaded"
assert_no_dir "$lists/github-actions" "the every-scenario allowlist is gone"
assert_contains "$(cat "$lists/ravencia-private")" "172.16.0.0/12" "private ranges stay an allowlist"
assert_eq 0 "$(find "$postoverflows" -name '.ravencia-*' | wc -l | tr -d ' ')" "no write-test file left"

run_refresh
assert_eq 0 "$refresh_status" "an unchanged list passes"
assert_contains "$output" "unchanged" "says unchanged"
assert_not_contains "$(cat "$FAKE_LOG")" "systemctl reload" "no reload when nothing changed"

# A refused configuration puts the previous file back and keeps the old allowlist.
cp "$whitelist" "$work/accepted.yaml"
printf '10.255.255.0/24\n' >> "$FAKE_RANGES"
printf '10.0.1.0/24\n' > "$lists/github-actions"
export FAKE_CONFIG_TEST_FAIL=1
run_refresh
unset FAKE_CONFIG_TEST_FAIL
assert_eq 1 "$refresh_status" "a refused configuration fails the run"
if cmp -s "$whitelist" "$work/accepted.yaml"; then pass "the previous whitelist is back"; else fail "the previous whitelist was not restored"; fi
assert_contains "$(cat "$lists/github-actions")" "10.0.1.0/24" "the old allowlist stays when the new whitelist is refused"

export FAKE_RELOAD_FAIL=1
run_refresh
unset FAKE_RELOAD_FAIL
assert_eq 1 "$refresh_status" "a failed reload fails the run"
if cmp -s "$whitelist" "$work/accepted.yaml"; then pass "the previous whitelist is back after a failed reload"; else fail "not restored after a failed reload"; fi
assert_contains "$(cat "$lists/github-actions")" "10.0.1.0/24" "the old allowlist stays after a failed reload"

# A short answer from GitHub changes nothing.
head -n 10 "$FAKE_RANGES" > "$work/short"
cp "$FAKE_RANGES" "$work/full"
cp "$work/short" "$FAKE_RANGES"
run_refresh
cp "$work/full" "$FAKE_RANGES"
assert_eq 1 "$refresh_status" "a short GitHub answer fails"
if cmp -s "$whitelist" "$work/accepted.yaml"; then pass "a short answer leaves the whitelist as it was"; else fail "a short answer changed the whitelist"; fi

# The marker with a read-only directory (an older unit): the old allowlist is kept current and the run fails.
rm -f -- "$whitelist"
chmod 0555 "$postoverflows"
if touch "$postoverflows/probe" 2>/dev/null; then
  rm -f -- "$postoverflows/probe"
  chmod 0755 "$postoverflows"
  pass "read-only directory case skipped (this file system ignores chmod: Windows or root)"
else
  run_refresh
  chmod 0755 "$postoverflows"
  assert_eq 1 "$refresh_status" "a marker without a writable directory fails"
  assert_contains "$output" "keeping the github-actions allowlist" "says the owner step is pending"
  assert_eq 1202 "$(wc -l < "$lists/github-actions" | tr -d ' ')" "the old allowlist is kept current"
fi

# Rollback: deleting the marker goes back to the old allowlist on the next run.
rm -f -- "$marker" "$lists/github-actions"
run_refresh
assert_eq 0 "$refresh_status" "after the marker is deleted the run passes"
assert_eq 1202 "$(wc -l < "$lists/github-actions" | tr -d ' ')" "after the marker is deleted the old allowlist is back"

finish "crowdsec allowlist refresh"
