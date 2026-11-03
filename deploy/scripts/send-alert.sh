#!/usr/bin/env bash
set -Eeuo pipefail

# Sends one operator alert to every configured channel.
# Usage: send-alert.sh "<message>"
#
# Channels (root-owned files in deploy/secrets/, never committed):
#   alert_smtp.txt         e-mail through Gmail SMTP (security plan Y1); three lines:
#                            1. sender Gmail address  2. recipient address  3. 16-character app password
#                          Sent with curl to smtps://smtp.gmail.com:465 as an RFC 5322 message (UTF-8).
#   alert_webhook_url.txt  one chat webhook URL:
#                            Telegram: https://api.telegram.org/bot<TOKEN>/sendMessage?chat_id=<CHAT_ID>
#                            Slack (or Slack-compatible, e.g. Discord .../slack): https://hooks.slack.com/services/...
# When both exist the alert goes to both; the exit code is 1 when any configured channel failed.
# When neither exists the alert is skipped with a note on stderr (exit 0), so the units work before an
# operator configures a channel; every deploy warns about it until one is configured.
# Never prints the URL or the password: the SMTP credentials reach curl through a 0600 config file,
# not its command line (so they do not show in ps).
# Test hooks: RAVENCIA_ALERT_URL_FILE, RAVENCIA_ALERT_SMTP_FILE, RAVENCIA_ALERT_SMTP_URL.

message="${1:?usage: send-alert.sh <message>}"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
url_file="${RAVENCIA_ALERT_URL_FILE:-$repository_root/deploy/secrets/alert_webhook_url.txt}"
smtp_file="${RAVENCIA_ALERT_SMTP_FILE:-$repository_root/deploy/secrets/alert_smtp.txt}"
smtp_url="${RAVENCIA_ALERT_SMTP_URL:-smtps://smtp.gmail.com:465}"

# Prints a root-owned secret file (directly when readable, else through sudo); nothing when missing.
read_secret() {
  if [[ -r "$1" ]]; then
    cat -- "$1"
  elif sudo -n test -s "$1" 2>/dev/null; then
    sudo -n cat -- "$1"
  fi
}

# One line, no control characters, bounded length.
host_name="$(hostname -s 2>/dev/null || echo server)"
clean_message="$(printf '%s' "$message" | tr '\r\n\t' '   ' | tr -d '\000-\037')"
text="[Ravencia $host_name] $clean_message"
text="${text:0:900}"

channels=0
failed=0

send_webhook() {
  local url="$1" chat_id escaped
  local -a curl_common=(curl --silent --show-error --fail --max-time 15 --retry 2 --retry-delay 5 --output /dev/null)
  [[ "$url" == https://* ]] || { echo "alert_webhook_url.txt must contain an https:// URL; webhook alert skipped." >&2; return 1; }
  if [[ "$url" == https://api.telegram.org/* ]]; then
    chat_id="$(sed -nE 's/.*[?&]chat_id=([^&]+).*/\1/p' <<<"$url")"
    [[ -n "$chat_id" ]] || { echo "Telegram alert URL needs ?chat_id=<id>; alert skipped." >&2; return 1; }
    "${curl_common[@]}" --data-urlencode "chat_id=$chat_id" --data-urlencode "text=$text" "${url%%\?*}" \
      || { echo "Telegram alert could not be delivered." >&2; return 1; }
  else
    escaped="${text//\\/\\\\}"
    escaped="${escaped//\"/\\\"}"
    "${curl_common[@]}" -H 'Content-Type: application/json' --data "{\"text\":\"$escaped\"}" "$url" \
      || { echo "Webhook alert could not be delivered." >&2; return 1; }
  fi
}

send_email() {
  local from="$1" recipients_line="$2" password="$3" config subject encoded_subject status=0 recipient
  local address_pattern='^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$'
  local -a recipients rcpt_args=()
  IFS=',' read -r -a recipients <<<"$recipients_line"
  for recipient in "${recipients[@]}"; do
    [[ "$recipient" =~ $address_pattern ]] || { recipients=(); break; }
    rcpt_args+=(--mail-rcpt "$recipient")
  done
  [[ "$from" =~ $address_pattern && ${#recipients[@]} -gt 0 && -n "$password" ]] \
    || { echo "alert_smtp.txt needs three lines: sender address, recipient address(es) separated by commas, app password; e-mail alert skipped." >&2; return 1; }
  subject="[Ravencia] ${clean_message:0:80}"
  # RFC 2047: the subject may carry Turkish letters.
  encoded_subject="=?UTF-8?B?$(printf '%s' "$subject" | base64 | tr -d '\n')?="
  config="$(umask 077 && mktemp)"
  # curl config syntax: a quoted value with backslash and double quote escaped.
  local user_value="$from:$password"
  user_value="${user_value//\\/\\\\}"
  user_value="${user_value//\"/\\\"}"
  printf 'user = "%s"\n' "$user_value" > "$config"
  {
    printf 'From: Ravencia <%s>\n' "$from"
    printf 'To: %s\n' "$(printf '<%s>, ' "${recipients[@]}" | sed 's/, $//')"
    printf 'Subject: %s\n' "$encoded_subject"
    printf 'Date: %s\n' "$(LC_ALL=C date -R)"
    printf 'Message-ID: <%s.%s.ravencia@%s>\n' "$(date -u +%s)" "$$" "$host_name"
    printf 'MIME-Version: 1.0\n'
    printf 'Content-Type: text/plain; charset=UTF-8\n'
    printf 'Content-Transfer-Encoding: 8bit\n'
    printf '\n'
    printf '%s\n' "$message"
    printf '\n-- \n%s, %s\n' "$host_name" "$(date -u '+%Y-%m-%d %H:%M:%S UTC')"
  } | curl --silent --show-error --max-time 30 --retry 2 --retry-delay 5 --ssl-reqd --crlf \
        --config "$config" --url "$smtp_url" --mail-from "$from" "${rcpt_args[@]}" --upload-file - || status=$?
  rm -f -- "$config"
  (( status == 0 )) || { echo "E-mail alert could not be delivered (curl exit $status)." >&2; return 1; }
}

# The panel's settings when the API answers (alert-settings-sync.sh also refreshes the host copies), else the cache,
# else deploy/secrets/alert_smtp.txt. RAVENCIA_ALERT_SKIP_SYNC=true reads the file only (tests).
smtp_lines=()
if [[ "${RAVENCIA_ALERT_SKIP_SYNC:-false}" == true ]]; then
  mapfile -t smtp_lines < <(read_secret "$smtp_file" | tr -d '\r' | head -n 3)
else
  mapfile -t smtp_lines < <(RAVENCIA_ALERT_SMTP_FILE="$smtp_file" "$script_dir/alert-settings-sync.sh" --print 2>/dev/null | tr -d '\r' | head -n 3)
fi
if ((${#smtp_lines[@]})); then
  channels=$((channels + 1))
  send_email "$(tr -d '[:space:]' <<<"${smtp_lines[0]:-}")" "$(tr -d '[:space:]' <<<"${smtp_lines[1]:-}")" \
    "$(tr -d '[:space:]' <<<"${smtp_lines[2]:-}")" || failed=$((failed + 1))
fi
unset smtp_lines

url="$(read_secret "$url_file" | head -n 1 | tr -d '[:space:]' || true)"
if [[ -n "$url" ]]; then
  channels=$((channels + 1))
  send_webhook "$url" || failed=$((failed + 1))
fi

if (( channels == 0 )); then
  # Still exit 0 (the units must not fail for it), but leave a trace in the journal
  # and the deploy log; deploy scripts also warn (warn_when_alerts_unconfigured).
  echo "No alert channel is configured (deploy/secrets/alert_smtp.txt or alert_webhook_url.txt); alert not sent: ${message:0:200}" >&2
  exit 0
fi
(( failed == 0 )) || exit 1
echo "Alert sent to $channels channel(s)."
