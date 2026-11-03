#!/usr/bin/env bash
set -Eeuo pipefail

# Keeps CrowdSec's allowlists current (security plan B5); runs daily as root from a
# root-owned copy (/usr/local/sbin/ravencia-crowdsec-allowlist, ravencia-crowdsec-allowlist.timer):
#   github-actions    GitHub Actions runner ranges, key "actions" of https://api.github.com/meta
#                     (the Deploy workflow connects from them; a ban would stop deploys)
#   ravencia-private  loopback, private and link-local ranges (Docker bridges, the host)
# An allowlist applies to every decision source, local scenarios and community blocklists alike.
# Entries are added and removed by difference, so a failed download changes nothing.

(( EUID == 0 )) || { echo "Run as root." >&2; exit 1; }
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

curl --silent --show-error --fail --max-time 60 --retry 3 --retry-delay 10 \
  -H 'Accept: application/vnd.github+json' https://api.github.com/meta -o "$work/meta.json"
jq -r '.actions[]' "$work/meta.json" | grep -E '^[0-9a-fA-F:.]+/[0-9]+$' > "$work/github-actions" || true
count="$(wc -l < "$work/github-actions")"
# A truncated or changed answer must not empty the list (it had ~8,000 ranges in November 2026).
(( count >= 1000 )) || { echo "GitHub meta returned only $count Actions ranges; allowlist left unchanged." >&2; exit 1; }
ensure_list github-actions "GitHub Actions runner ranges (api.github.com/meta, refreshed daily)"
sync_list github-actions "$work/github-actions"

printf '%s\n' 127.0.0.0/8 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 169.254.0.0/16 100.64.0.0/10 ::1/128 fc00::/7 fe80::/10 > "$work/ravencia-private"
ensure_list ravencia-private "Loopback, private and link-local ranges"
sync_list ravencia-private "$work/ravencia-private"
