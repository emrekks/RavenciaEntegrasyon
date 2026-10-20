#!/usr/bin/env bash
set -Eeuo pipefail

# Posts one short line to the operator's chat webhook.
# Usage: send-alert.sh "<message>"
#
# The URL is read from deploy/secrets/alert_webhook_url.txt (one line, root-owned,
# never committed). When the file is missing or empty the alert is skipped
# silently, so the units work before an operator configures a channel.
#   Telegram: https://api.telegram.org/bot<TOKEN>/sendMessage?chat_id=<CHAT_ID>
#   Slack (or any Slack-compatible incoming webhook, e.g. Discord .../slack):
#             https://hooks.slack.com/services/...  -> JSON {"text": "..."}
# Never prints the URL: it carries the bot token.

message="${1:?usage: send-alert.sh <message>}"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
url_file="${RAVENCIA_ALERT_URL_FILE:-$repository_root/deploy/secrets/alert_webhook_url.txt}"

read_url() {
  if [[ -r "$url_file" ]]; then
    head -n 1 -- "$url_file"
  elif sudo -n test -s "$url_file" 2>/dev/null; then
    sudo -n head -n 1 -- "$url_file"
  fi
}
url="$(read_url | tr -d '[:space:]' || true)"
[[ -n "$url" ]] || exit 0
[[ "$url" == https://* ]] || { echo "alert_webhook_url.txt must contain an https:// URL; alert skipped." >&2; exit 0; }

# One line, no control characters, bounded length.
text="[Ravencia $(hostname -s)] $(printf '%s' "$message" | tr '\r\n\t' '   ' | tr -d '\000-\037')"
text="${text:0:900}"

curl_common=(curl --silent --show-error --fail --max-time 15 --retry 2 --retry-delay 5 --output /dev/null)
if [[ "$url" == https://api.telegram.org/* ]]; then
  chat_id="$(sed -nE 's/.*[?&]chat_id=([^&]+).*/\1/p' <<<"$url")"
  [[ -n "$chat_id" ]] || { echo "Telegram alert URL needs ?chat_id=<id>; alert skipped." >&2; exit 0; }
  "${curl_common[@]}" --data-urlencode "chat_id=$chat_id" --data-urlencode "text=$text" "${url%%\?*}" \
    || { echo "Telegram alert could not be delivered." >&2; exit 1; }
else
  escaped="${text//\\/\\\\}"
  escaped="${escaped//\"/\\\"}"
  "${curl_common[@]}" -H 'Content-Type: application/json' --data "{\"text\":\"$escaped\"}" "$url" \
    || { echo "Webhook alert could not be delivered." >&2; exit 1; }
fi
