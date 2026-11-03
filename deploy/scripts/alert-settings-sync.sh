#!/usr/bin/env bash
set -Eeuo pipefail

# Keeps the host's alert e-mail settings in step with the panel (Settings → Alarm Bildirimleri). Runs as root:
# from send-alert.sh before each alert, and from every deploy (fast-deploy.sh, through sudo).
#   - asks the API over loopback (GET /internal/alert-settings; Caddy and the API answer it only from the server's
#     own networks, and only with the token in deploy/secrets/alert_sync_token.txt);
#   - 200: the panel's settings (three lines: sender, recipients separated by commas, app password) are written to
#     the cache /var/lib/ravencia-alerts/alert_smtp.txt and, when writable (deploy), to deploy/secrets/alert_smtp.txt;
#   - 204: the panel has none yet; an existing deploy/secrets/alert_smtp.txt (e.g. written with
#     `sudo ravencia-alarm-eposta`) is imported into the panel once (POST /internal/alert-settings/import);
#   - API down or unreachable: nothing changes; send-alert.sh uses the cache, then the secrets file.
# The panel is the source of truth once it has settings: a later hand edit of the secrets file is overwritten.
# --print: prints the settings send-alert.sh should use (panel, else cache, else secrets file); nothing else on stdout.
# Never prints the token or the password except through --print's stdout (a pipe to send-alert.sh).
# Test hooks: RAVENCIA_ALERT_SMTP_FILE, RAVENCIA_ALERT_CACHE_FILE, RAVENCIA_ALERT_TOKEN_FILE, RAVENCIA_ALERT_SYNC_URL,
# RAVENCIA_ALERT_SYNC_RESOLVE (curl --resolve value; "none" to skip).

print=false
[[ "${1:-}" != --print ]] || print=true

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
secrets_dir="$repository_root/deploy/secrets"
smtp_file="${RAVENCIA_ALERT_SMTP_FILE:-$secrets_dir/alert_smtp.txt}"
cache_file="${RAVENCIA_ALERT_CACHE_FILE:-/var/lib/ravencia-alerts/alert_smtp.txt}"
token_file="${RAVENCIA_ALERT_TOKEN_FILE:-$secrets_dir/alert_sync_token.txt}"

read_secret() {
  if [[ -r "$1" ]]; then cat -- "$1"; elif sudo -n test -s "$1" 2>/dev/null; then sudo -n cat -- "$1"; fi
}

site_address="https://panel.ravencia.com"
environment_value="$(read_secret "$secrets_dir/production.env" 2>/dev/null | sed -n 's/^MARKETPLACEHUB_SITE_ADDRESS=//p' | head -n 1 | tr -d '[:space:]' || true)"
[[ "$environment_value" != https://* ]] || site_address="${environment_value%/}"
sync_url="${RAVENCIA_ALERT_SYNC_URL:-$site_address/internal/alert-settings}"
authority="${site_address#https://}"
authority="${authority%%/*}"
resolve="${RAVENCIA_ALERT_SYNC_RESOLVE:-${authority%%:*}:443:127.0.0.1}"

work="$(umask 077 && mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

# Three valid-looking lines (sender, recipients, password) or nothing.
valid_settings() {
  local -a lines
  mapfile -t lines < <(tr -d '\r' < "$1" | head -n 3)
  [[ ${#lines[@]} -eq 3 && "${lines[0]}" == *@* && "${lines[1]}" == *@* && -n "${lines[2]// /}" ]]
}

# Writes $1 to $2 (0600 root) when the content differs; quiet when the target is read-only here.
store() {
  local source="$1" target="$2"
  [[ -n "$target" ]] || return 0
  if [[ -r "$target" ]] && cmp -s -- "$source" "$target"; then return 0; fi
  mkdir -p -- "$(dirname -- "$target")" 2>/dev/null || return 0
  chmod 0700 -- "$(dirname -- "$target")" 2>/dev/null || true
  local temporary="$target.tmp.$$"
  if (umask 077 && cp -- "$source" "$temporary") 2>/dev/null && mv -f -- "$temporary" "$target" 2>/dev/null; then
    $print || echo "Alert e-mail settings refreshed in $target."
  else
    rm -f -- "$temporary" 2>/dev/null || true
  fi
}

token="$(read_secret "$token_file" | tr -d '[:space:]' || true)"
status=""
if [[ -n "$token" ]]; then
  # The token reaches curl through a 0600 config file, not its command line.
  printf 'header = "X-Ravencia-Host-Token: %s"\n' "$token" > "$work/curl.conf"
  resolve_args=()
  [[ "$resolve" == none ]] || resolve_args=(--resolve "$resolve")
  status="$(curl --silent --max-time 8 --config "$work/curl.conf" "${resolve_args[@]}" \
    --output "$work/panel.txt" --write-out '%{http_code}' "$sync_url" 2>/dev/null || true)"
  case "$status" in
    200)
      if valid_settings "$work/panel.txt"; then
        store "$work/panel.txt" "$cache_file"
        store "$work/panel.txt" "$smtp_file"
        if $print; then cat -- "$work/panel.txt"; exit 0; fi
      fi
      ;;
    204)
      # The panel has no settings yet: offer the host file once.
      if read_secret "$smtp_file" > "$work/host.txt" 2>/dev/null && valid_settings "$work/host.txt"; then
        import_status="$(curl --silent --max-time 8 --config "$work/curl.conf" "${resolve_args[@]}" -H 'Content-Type: text/plain; charset=utf-8' \
          --data-binary "@$work/host.txt" --output /dev/null --write-out '%{http_code}' "$sync_url/import" 2>/dev/null || true)"
        $print || echo "Alert e-mail settings import into the panel: HTTP ${import_status:-000}."
      fi
      ;;
    *)
      $print || echo "The panel did not answer the alert settings request (HTTP ${status:-000}); the host files are used." >&2
      ;;
  esac
fi

if $print; then
  if [[ -s "$cache_file" && -r "$cache_file" ]] && valid_settings "$cache_file"; then cat -- "$cache_file"
  else read_secret "$smtp_file" || true
  fi
fi
