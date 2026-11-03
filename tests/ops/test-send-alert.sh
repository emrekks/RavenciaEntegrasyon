#!/usr/bin/env bash
# deploy/scripts/send-alert.sh with a fake curl (no network): e-mail, webhook, both and none.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
script="$repository_root/deploy/scripts/send-alert.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
bin="$work/bin"
calls="$work/calls"
mkdir -p "$bin" "$calls"

# Fake curl: each call gets a directory with its argv (one per line), stdin and the --config file it was given.
cat > "$bin/curl" <<'STUB'
#!/usr/bin/env bash
set -eu
n=$(find "$FAKE_CURL_DIR" -mindepth 1 -maxdepth 1 -type d | wc -l)
dir="$FAKE_CURL_DIR/$n"
mkdir -p "$dir"
printf '%s\n' "$@" > "$dir/argv"
config=""
previous=""
for arg in "$@"; do
  [[ "$previous" != --config ]] || config="$arg"
  previous="$arg"
done
if [[ -n "$config" ]]; then
  cp -- "$config" "$dir/config"
  stat -c '%a' -- "$config" > "$dir/config_mode"
fi
if printf '%s\n' "$@" | grep -qx -- '--upload-file'; then cat > "$dir/stdin"; fi
exit "${FAKE_CURL_EXIT:-0}"
STUB
chmod +x "$bin/curl"

smtp="$work/alert_smtp.txt"
webhook="$work/alert_webhook_url.txt"
password="abcdefghijklmnop"

run_alert() {
  local status=0
  rm -rf -- "${calls:?}"/*
  output="$(PATH="$bin:$PATH" FAKE_CURL_DIR="$calls" RAVENCIA_ALERT_SMTP_FILE="$smtp" RAVENCIA_ALERT_URL_FILE="$webhook" \
    bash "$script" "$1" 2>&1)" || status=$?
  exit_code="$status"
  call_count="$(find "$calls" -mindepth 1 -maxdepth 1 -type d | wc -l)"
}

echo "send-alert channels"

run_alert "Disk dolu"
assert_eq 0 "$exit_code" "no channel: exit 0"
assert_eq 0 "$call_count" "no channel: curl not called"
assert_contains "$output" "No alert channel is configured" "no channel: note on stderr"

printf 'sender@gmail.com\nowner@example.com\n%s\n' "$password" > "$smtp"
run_alert "Ravencia alarm testi — e-posta kanalı çalışıyor"
assert_eq 0 "$exit_code" "e-mail: exit 0"
assert_eq 1 "$call_count" "e-mail: one curl call"
argv="$(cat "$calls/0/argv")"
assert_contains "$argv" "smtps://smtp.gmail.com:465" "e-mail: Gmail SMTPS URL"
assert_contains "$argv" "--ssl-reqd" "e-mail: TLS required"
assert_contains "$argv" "owner@example.com" "e-mail: recipient"
assert_not_contains "$argv" "$password" "e-mail: password not on the command line"
assert_contains "$(cat "$calls/0/config")" "user = \"sender@gmail.com:$password\"" "e-mail: credentials in the curl config"
assert_eq 600 "$(cat "$calls/0/config_mode")" "e-mail: config file is 0600"
mail="$(cat "$calls/0/stdin")"
assert_contains "$mail" "From: Ravencia <sender@gmail.com>" "e-mail: From header"
assert_contains "$mail" "To: <owner@example.com>" "e-mail: To header"
assert_contains "$mail" "Subject: =?UTF-8?B?" "e-mail: encoded Subject"
expected_subject="=?UTF-8?B?$(printf '%s' "[Ravencia] Ravencia alarm testi — e-posta kanalı çalışıyor" | base64 | tr -d '\n')?="
assert_contains "$mail" "Subject: $expected_subject" "e-mail: Subject is [Ravencia] + message"
assert_contains "$mail" "Date: " "e-mail: Date header"
assert_contains "$mail" "Content-Type: text/plain; charset=UTF-8" "e-mail: UTF-8 body"
assert_contains "$mail" "Ravencia alarm testi — e-posta kanalı çalışıyor" "e-mail: body is the message"
assert_not_contains "$output" "$password" "e-mail: password not printed"

printf 'https://hooks.slack.com/services/T000/B000/XXXX\n' > "$webhook"
run_alert "Both"
assert_eq 0 "$exit_code" "both: exit 0"
assert_eq 2 "$call_count" "both: e-mail and webhook"

export FAKE_CURL_EXIT=67
run_alert "Bad login"
unset FAKE_CURL_EXIT
assert_eq 1 "$exit_code" "failed delivery: exit 1"
assert_not_contains "$output" "$password" "failed delivery: password not printed"

rm -f -- "$smtp"
run_alert "Webhook only"
assert_eq 0 "$exit_code" "webhook only: exit 0"
assert_eq 1 "$call_count" "webhook only: one curl call"
assert_contains "$(cat "$calls/0/argv")" "https://hooks.slack.com/services/T000/B000/XXXX" "webhook only: webhook URL"

printf 'not-an-address\nowner@example.com\n%s\n' "$password" > "$smtp"
rm -f -- "$webhook"
run_alert "Broken file"
assert_eq 1 "$exit_code" "malformed alert_smtp.txt: exit 1"
assert_eq 0 "$call_count" "malformed alert_smtp.txt: curl not called"

finish "send-alert"
