#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
unit_dir="$script_dir/../systemd"

sudo -n install -m 0644 "$unit_dir/ravencia-backup.service" /etc/systemd/system/ravencia-backup.service
sudo -n install -m 0644 "$unit_dir/ravencia-backup.timer" /etc/systemd/system/ravencia-backup.timer
sudo -n systemctl daemon-reload
sudo -n systemctl enable --now ravencia-backup.timer
sudo -n systemctl status --no-pager ravencia-backup.timer
