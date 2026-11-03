#!/usr/bin/env bash
set -Eeuo pipefail

# Installs/refreshes the host's operational systemd units. Every deploy runs it,
# so unit changes in the repository reach the host with the next release:
#   ravencia-backup.timer         verified backup twice a day (08:59 and 20:59 UTC)
#   ravencia-restore-drill.timer  monthly isolated restore drill
#   ravencia-healthwatch.timer    5-minute health watch + API self-heal
#   ravencia-maintenance.timer    monthly OS/kernel updates, reboot only when required
#   ravencia-alert@.service       alert sender used by OnFailure= and the watch
#   ravencia-deploy-guard.service holds a host shutdown while a deploy holds its lock
#                                 (with the session-.scope.d ordering drop-in)
# Backup, restore drill, health watch, maintenance and alerts run as root with systemd
# hardening (no sudo), from the root-owned copy in /usr/local/lib/ravencia; only the deploy
# guard runs as ubuntu from the repository.
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
  ravencia-maintenance.service
  ravencia-maintenance.timer
  ravencia-deploy-guard.service
)
timers=(ravencia-backup.timer ravencia-restore-drill.timer ravencia-healthwatch.timer ravencia-maintenance.timer)
# CrowdSec's allowlist refresh, once install-crowdsec.sh has installed CrowdSec.
if command -v cscli >/dev/null 2>&1; then
  units+=(ravencia-crowdsec-allowlist.service ravencia-crowdsec-allowlist.timer)
  timers+=(ravencia-crowdsec-allowlist.timer)
fi

# The root units must not run files the ubuntu account can change between deploys (the
# repository belongs to it), so they run a root-owned copy of deploy/{scripts,backup,compose}
# under /usr/local/lib/ravencia, replaced as a whole on every deploy. deploy/secrets there is a
# symlink to the repository's root-owned secrets directory. Residual: the deploy itself still runs
# as ubuntu with passwordless sudo (security plan A4 needs that chain moved first).
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
root_copy=/usr/local/lib/ravencia
staging="$(sudo -n mktemp -d /usr/local/lib/.ravencia-new.XXXXXX)"
sudo -n install -d -m 0755 "$staging/deploy"
for part in scripts backup compose; do
  sudo -n cp -R -- "$repository_root/deploy/$part" "$staging/deploy/$part"
done
sudo -n ln -s -- "$repository_root/deploy/secrets" "$staging/deploy/secrets"
sudo -n chown -R -h root:root "$staging"
sudo -n chmod -R go-w,a+rX "$staging"
sudo -n chmod 0755 "$staging"
sudo -n rm -rf -- "$root_copy.old"
if sudo -n test -e "$root_copy"; then sudo -n mv -- "$root_copy" "$root_copy.old"; fi
sudo -n mv -- "$staging" "$root_copy"
sudo -n rm -rf -- "$root_copy.old"
# Before 4 Nov 2026 the maintenance script had its own copy here.
sudo -n rm -f /usr/local/sbin/ravencia-maintenance

for unit in "${units[@]}"; do
  sudo -n install -m 0644 "$unit_dir/$unit" "/etc/systemd/system/$unit"
done
# Operator switches for the maintenance window; kept when it already exists.
if ! sudo -n test -e /etc/default/ravencia-maintenance; then
  printf '%s\n' \
    '# Read by ravencia-maintenance.service (deploy/scripts/monthly-maintenance.sh).' \
    '# hold: Docker Engine/containerd stay on apt hold. allow: upgrade them in the next window.' \
    'RAVENCIA_MAINTENANCE_DOCKER=hold' \
    '# auto: reboot when /var/run/reboot-required exists. never: only report it.' \
    'RAVENCIA_MAINTENANCE_REBOOT=auto' \
    | sudo -n tee /etc/default/ravencia-maintenance >/dev/null
fi
sudo -n install -d -m 0755 /etc/systemd/system/session-.scope.d
sudo -n install -m 0644 "$unit_dir/session-.scope.d/ravencia-deploy-guard.conf" /etc/systemd/system/session-.scope.d/ravencia-deploy-guard.conf
sudo -n systemctl daemon-reload
sudo -n systemctl enable --now "${timers[@]}"
# Starting it runs /bin/true; only its ExecStop (at shutdown) does any work.
sudo -n systemctl enable --now ravencia-deploy-guard.service
sudo -n systemctl list-timers --no-pager "${timers[@]}"
