#!/usr/bin/env bash
set -Eeuo pipefail

# Monthly maintenance window (ravencia-maintenance.timer, first Sunday of the month
# 01:00 UTC = 04:00 Istanbul). Runs as root from a root-owned copy
# (/usr/local/sbin/ravencia-maintenance, installed by install-backup-timer.sh):
#   1. waits for a running deploy to release ~ubuntu/deploy-logs/.lock and holds the
#      lock itself, so no deploy starts while packages change;
#   2. apt-get update + full-upgrade (kernel, OS, security);
#   3. Docker Engine / containerd stay on apt hold unless
#      RAVENCIA_MAINTENANCE_DOCKER=allow is set in /etc/default/ravencia-maintenance;
#      then they are unheld, upgraded and held again. The daemon runs with
#      live-restore (deploy/docker/daemon.json), so a daemon restart keeps the containers.
#      Docker Compose (docker-compose-plugin) is always left on hold: deploys depend on it.
#   4. reboots only when /var/run/reboot-required exists. Restart policies bring the
#      stack back; healthwatch skips the first 10 minutes after boot.

lock_file="${RAVENCIA_DEPLOY_LOCK:-/home/ubuntu/deploy-logs/.lock}"
lock_wait="${RAVENCIA_MAINTENANCE_LOCK_WAIT_SECONDS:-3600}"
docker_policy="${RAVENCIA_MAINTENANCE_DOCKER:-hold}"
reboot_policy="${RAVENCIA_MAINTENANCE_REBOOT:-auto}"
engine_packages=(docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-ce-rootless-extras)
held_packages=("${engine_packages[@]}" docker-compose-plugin)

[[ "$docker_policy" == hold || "$docker_policy" == allow ]] || { echo "RAVENCIA_MAINTENANCE_DOCKER must be hold or allow." >&2; exit 2; }
[[ "$reboot_policy" == auto || "$reboot_policy" == never ]] || { echo "RAVENCIA_MAINTENANCE_REBOOT must be auto or never." >&2; exit 2; }
(( EUID == 0 )) || { echo "Run as root (ravencia-maintenance.service)." >&2; exit 1; }

export DEBIAN_FRONTEND=noninteractive
# needrestart lists outdated services instead of restarting them (Docker among them);
# a reboot follows whenever the kernel or core libraries changed.
export NEEDRESTART_MODE=l NEEDRESTART_SUSPEND=1

if [[ -e "$lock_file" ]]; then
  # Opened read-only: flock does not need write access and the file is not created.
  exec 9<"$lock_file"
  echo "Waiting up to ${lock_wait}s for the deploy lock."
  flock --wait "$lock_wait" 9 || { echo "A deploy still holds $lock_file after ${lock_wait}s; maintenance skipped this month." >&2; exit 1; }
  echo "Deploy lock held for the maintenance window."
fi

apt_get=(apt-get -o DPkg::Lock::Timeout=900 -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold -y)
apt-get -o DPkg::Lock::Timeout=900 update -q

if [[ "$docker_policy" == allow ]]; then
  echo "Docker Engine upgrade allowed (RAVENCIA_MAINTENANCE_DOCKER=allow)."
  apt-mark unhold "${engine_packages[@]}"
fi
status=0
"${apt_get[@]}" full-upgrade || status=$?
# Held again in every case, also after a failed upgrade.
apt-mark hold "${held_packages[@]}" >/dev/null
(( status == 0 )) || { echo "apt-get full-upgrade failed with exit code $status." >&2; exit "$status"; }
"${apt_get[@]}" autoremove --purge

dpkg-query -W -f '${Package} ${Version}\n' "${held_packages[@]}" linux-image-virtual 2>/dev/null || true

if [[ -e /var/run/reboot-required ]]; then
  echo "Reboot required by: $(tr '\n' ' ' < /var/run/reboot-required.pkgs 2>/dev/null || echo unknown)"
  if [[ "$reboot_policy" == never ]]; then
    echo "RAVENCIA_MAINTENANCE_REBOOT=never: not rebooting."
    exit 0
  fi
  # Release the deploy lock first: ravencia-deploy-guard.service waits for it at shutdown.
  exec 9<&-
  echo "Rebooting now."
  systemctl reboot --no-block
else
  echo "No reboot required."
fi
