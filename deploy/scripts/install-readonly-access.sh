#!/usr/bin/env bash
set -Eeuo pipefail

# Server setup for the read-only "ravencia-ro" login used by Claude's /prod-sql, /prod-logs and
# ship.sh (security plan A2/H6). Run as ubuntu:
#   - installs readonly-entrypoint.sh root-owned at /usr/local/bin/ravencia-ro;
#   - creates/refreshes the PostgreSQL role ravencia_ro (install-readonly-db-role.sh);
#   - with a public key line: creates ravencia-ro (no password), whose root-owned
#     authorized_keys runs nothing but that script, and whose only sudo right is running it
#     as root (/etc/sudoers.d/ravencia-ro); adds ravencia-ro to sshd AllowUsers.
# Usage: install-readonly-access.sh ["<ssh-ed25519 public key line>"]

public_key="${1:-}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
entrypoint=/usr/local/bin/ravencia-ro
ro_user=ravencia-ro
sudo -n install -o root -g root -m 0755 -- "$script_dir/readonly-entrypoint.sh" "$entrypoint"
echo "Read-only entrypoint refreshed: $entrypoint"
# The `sql` request runs as this database role; it is the real read-only boundary.
"$script_dir/install-readonly-db-role.sh"
[[ -n "$public_key" ]] || exit 0

[[ "$public_key" =~ ^ssh-ed25519\ [A-Za-z0-9+/=]+(\ [^[:space:]\"]+)?$ ]] || { echo "Public key must be a single ssh-ed25519 line." >&2; exit 2; }

if ! getent passwd "$ro_user" >/dev/null; then
  sudo -n useradd --create-home --shell /bin/sh --comment "Ravencia read-only checks (forced command)" "$ro_user"
  echo "User $ro_user created."
fi
sudo -n passwd --lock "$ro_user" >/dev/null
ro_home="$(getent passwd "$ro_user" | cut -d: -f6)"

sudo -n install -d -o root -g root -m 0755 "$ro_home/.ssh"
printf 'restrict,command="%s" %s\n' "$entrypoint" "$public_key" | sudo -n tee "$ro_home/.ssh/authorized_keys" >/dev/null
sudo -n chown root:root "$ro_home/.ssh/authorized_keys"
sudo -n chmod 0644 "$ro_home/.ssh/authorized_keys"

sudoers_tmp="$(mktemp)"
trap 'rm -f -- "$sudoers_tmp"' EXIT
printf '%s\n' \
  "# Installed by deploy/scripts/install-readonly-access.sh: the read-only login may only run" \
  "# the root-owned read-only entrypoint." \
  "$ro_user ALL=(root) NOPASSWD: $entrypoint" > "$sudoers_tmp"
sudo -n visudo -cf "$sudoers_tmp" >/dev/null
sudo -n install -o root -g root -m 0440 -- "$sudoers_tmp" /etc/sudoers.d/ravencia-ro

"$script_dir/sshd-allow-user.sh" "$ro_user"
echo "Read-only key authorised for $ro_user with a forced command."
