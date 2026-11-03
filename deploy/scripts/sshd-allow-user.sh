#!/usr/bin/env bash
set -Eeuo pipefail

# Adds a login to the AllowUsers line of the Ravencia sshd drop-in (security plan S4) and
# reloads sshd only when the result passes `sshd -t`; otherwise the drop-in is restored.
# Usage: sshd-allow-user.sh <user>

user="${1:?usage: sshd-allow-user.sh <user>}"
[[ "$user" =~ ^[a-z][a-z0-9-]*$ ]] || { echo "Invalid user name: $user" >&2; exit 2; }
dropin="${RAVENCIA_SSHD_DROPIN:-/etc/ssh/sshd_config.d/00-ravencia.conf}"

if ! sudo -n grep -qE '^AllowUsers ' "$dropin" 2>/dev/null; then
  echo "No AllowUsers line in $dropin; nothing to change."
  exit 0
fi
if sudo -n grep -qE "^AllowUsers( .*)? $user( |$)" "$dropin"; then
  echo "sshd AllowUsers already includes $user."
  exit 0
fi

# The backup's name does not end in .conf, so sshd never reads it.
sudo -n cp -p -- "$dropin" "$dropin.ravencia-backup"
sudo -n sed -i -E "s/^(AllowUsers .*)$/\1 $user/" "$dropin"
if sudo -n sshd -t; then
  sudo -n systemctl reload ssh
  sudo -n rm -f -- "$dropin.ravencia-backup"
  echo "sshd AllowUsers now includes $user."
else
  sudo -n mv -f -- "$dropin.ravencia-backup" "$dropin"
  echo "sshd rejected the change; $dropin was restored." >&2
  exit 1
fi
