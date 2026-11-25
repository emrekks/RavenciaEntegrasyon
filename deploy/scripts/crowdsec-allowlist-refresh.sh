#!/usr/bin/env bash
set -Eeuo pipefail

# Keeps CrowdSec's exemptions current (security plan B5); runs daily as root from a
# root-owned copy (/usr/local/lib/ravencia/deploy/scripts, ravencia-crowdsec-allowlist.timer):
#   ravencia/github-actions-ssh  postoverflow whitelist (s01-whitelist): GitHub Actions runner ranges, key "actions" of
#                     https://api.github.com/meta, exempt from the SSH scenarios (crowdsecurity/ssh-*) only. The Deploy
#                     workflow logs in over SSH from them; a ban would stop deploys. Their HTTP requests stay visible
#                     (scan report A-11: before 26 Nov they were an allowlist, exempt from every scenario).
#   ravencia-private  allowlist: loopback, private and link-local ranges (Docker bridges, the host)
# An allowlist applies to every decision source, local scenarios and community blocklists alike; the postoverflow
# whitelist only to local SSH alerts.
# The whitelist file is replaced only after "crowdsec -t" accepts it and CrowdSec reloads with it (else the previous
# file is put back and the run fails); only then is the old github-actions allowlist removed. A failed download changes
# nothing. Under the unit from before 26 Nov (ProtectSystem=full, /etc read-only) the file cannot be written: the old
# github-actions allowlist is then kept up to date as before until the owner reinstalls the unit
# (docs/CI-CD-DEPLOY.md, "CrowdSec").
# Test hooks: RAVENCIA_CROWDSEC_POSTOVERFLOW_DIR, RAVENCIA_CROWDSEC_SKIP_ROOT_CHECK=1.

if [[ "${RAVENCIA_CROWDSEC_SKIP_ROOT_CHECK:-}" != 1 ]]; then
  (( EUID == 0 )) || { echo "Run as root." >&2; exit 1; }
fi
postoverflow_dir="${RAVENCIA_CROWDSEC_POSTOVERFLOW_DIR:-/etc/crowdsec/postoverflows/s01-whitelist}"
postoverflow_file="$postoverflow_dir/ravencia-github-actions-ssh.yaml"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

ensure_list() {
  local name="$1" description="$2"
  cscli allowlists inspect "$name" -o json >/dev/null 2>&1 || cscli allowlists create "$name" --description "$description" >/dev/null
}

# Brings allowlist $1 to exactly the values listed in file $2 (one per line).
sync_list() {
  local name="$1" wanted="$2" current="$work/$1.current" start
  local -a to_add to_remove
  cscli allowlists inspect "$name" -o json | jq -r '(.items // [])[].value' | sort -u > "$current"
  sort -u -o "$wanted" "$wanted"
  mapfile -t to_add < <(comm -13 "$current" "$wanted")
  mapfile -t to_remove < <(comm -23 "$current" "$wanted")
  for ((start = 0; start < ${#to_add[@]}; start += 500)); do
    cscli allowlists add "$name" "${to_add[@]:start:500}" -d "managed by ravencia-crowdsec-allowlist" >/dev/null
  done
  for ((start = 0; start < ${#to_remove[@]}; start += 500)); do
    cscli allowlists remove "$name" "${to_remove[@]:start:500}" >/dev/null
  done
  echo "Allowlist $name: $(wc -l < "$wanted") entries (+${#to_add[@]} -${#to_remove[@]})."
}

# Writes the postoverflow whitelist for the ranges in file $1 to file $2.
write_postoverflow() {
  local ranges="$1" target="$2" range
  {
    echo "# Written by deploy/scripts/crowdsec-allowlist-refresh.sh (ravencia-crowdsec-allowlist.timer); do not edit."
    echo "name: ravencia/github-actions-ssh"
    echo "description: \"GitHub Actions runner ranges (api.github.com/meta), exempt from the SSH scenarios only: the Deploy workflow's SSH login\""
    echo "filter: \"evt.Overflow.Alert.GetScenario() startsWith 'crowdsecurity/ssh-'\""
    echo "whitelist:"
    echo "  reason: \"GitHub Actions deploy runner (SSH scenarios only)\""
    echo "  cidr:"
    while IFS= read -r range; do
      printf '    - "%s"\n' "$range"
    done < "$ranges"
  } > "$target"
}

# Puts the previous whitelist file back (or removes the new one) after a refused or failed reload.
restore_postoverflow() {
  if [[ -f "$work/previous.yaml" ]]; then
    install -m 0644 "$work/previous.yaml" "$postoverflow_file"
  else
    rm -f -- "$postoverflow_file"
  fi
  systemctl reload crowdsec || true
}

curl --silent --show-error --fail --max-time 60 --retry 3 --retry-delay 10 \
  -H 'Accept: application/vnd.github+json' https://api.github.com/meta -o "$work/meta.json"
jq -r '.actions[]' "$work/meta.json" | grep -E '^[0-9a-fA-F:.]+/[0-9]+$' > "$work/github-actions" || true
count="$(wc -l < "$work/github-actions")"
# A truncated or changed answer must not empty the list (it had ~8,000 ranges in November 2026).
(( count >= 1000 )) || { echo "GitHub meta returned only $count Actions ranges; exemptions left unchanged." >&2; exit 1; }
sort -u -o "$work/github-actions" "$work/github-actions"

if [[ -d "$postoverflow_dir" && -w "$postoverflow_dir" ]] && touch "$postoverflow_dir/.ravencia-write-test" 2>/dev/null; then
  rm -f -- "$postoverflow_dir/.ravencia-write-test"
  write_postoverflow "$work/github-actions" "$work/github-actions-ssh.yaml"
  if [[ -f "$postoverflow_file" ]] && cmp -s "$postoverflow_file" "$work/github-actions-ssh.yaml"; then
    echo "Postoverflow whitelist ravencia/github-actions-ssh: $count ranges, unchanged."
  else
    if [[ -f "$postoverflow_file" ]]; then cp -- "$postoverflow_file" "$work/previous.yaml"; fi
    install -m 0644 "$work/github-actions-ssh.yaml" "$postoverflow_file"
    if ! crowdsec -t >"$work/config-test.log" 2>&1; then
      tail -n 20 "$work/config-test.log" >&2
      restore_postoverflow
      echo "crowdsec -t refused the new postoverflow whitelist; the previous one is back and nothing else changed." >&2
      exit 1
    fi
    if ! systemctl reload crowdsec; then
      restore_postoverflow
      echo "CrowdSec did not reload with the new postoverflow whitelist; the previous one is back and nothing else changed." >&2
      exit 1
    fi
    echo "Postoverflow whitelist ravencia/github-actions-ssh: $count ranges, SSH scenarios only (reloaded)."
  fi
  # The SSH-only whitelist is in force: the old allowlist, which exempted the ranges from every scenario, goes.
  if cscli allowlists inspect github-actions -o json >/dev/null 2>&1; then
    cscli allowlists delete github-actions >/dev/null
    echo "Allowlist github-actions removed (its ranges are exempt from the SSH scenarios only now)."
  fi
else
  echo "Postoverflow directory $postoverflow_dir is not writable (unit from before 26 Nov 2026): keeping the github-actions allowlist; reinstall the unit with deploy/scripts/install-crowdsec.sh." >&2
  ensure_list github-actions "GitHub Actions runner ranges (api.github.com/meta, refreshed daily)"
  sync_list github-actions "$work/github-actions"
fi

printf '%s\n' 127.0.0.0/8 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 169.254.0.0/16 100.64.0.0/10 ::1/128 fc00::/7 fe80::/10 > "$work/ravencia-private"
ensure_list ravencia-private "Loopback, private and link-local ranges"
sync_list ravencia-private "$work/ravencia-private"
