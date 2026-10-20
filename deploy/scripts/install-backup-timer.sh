#!/usr/bin/env bash
set -Eeuo pipefail

# Installs/refreshes the host's operational systemd units. Every deploy runs it,
# so unit changes in the repository reach the host with the next release:
#   ravencia-backup.timer         nightly verified backup (20:30 UTC)
#   ravencia-restore-drill.timer  monthly isolated restore drill
#   ravencia-healthwatch.timer    5-minute health watch + API self-heal
#   ravencia-alert@.service       alert sender used by OnFailure= and the watch
# Alerts are delivered only after deploy/secrets/alert_webhook_url.txt exists.

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
unit_dir="$script_dir/../systemd"

units=(
  ravencia-alert@.service
  ravencia-backup.service
  ravencia-backup.timer
  ravencia-restore-drill.service
  ravencia-restore-drill.timer
  ravencia-healthwatch.service
  ravencia-healthwatch.timer
)
timers=(ravencia-backup.timer ravencia-restore-drill.timer ravencia-healthwatch.timer)

for unit in "${units[@]}"; do
  sudo -n install -m 0644 "$unit_dir/$unit" "/etc/systemd/system/$unit"
done
sudo -n systemctl daemon-reload
sudo -n systemctl enable --now "${timers[@]}"
sudo -n systemctl list-timers --no-pager "${timers[@]}"
