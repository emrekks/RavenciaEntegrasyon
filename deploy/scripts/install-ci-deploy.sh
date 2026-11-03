#!/usr/bin/env bash
set -Eeuo pipefail

# Server setup for the GitHub Actions deploy key (run as the repository owner, ubuntu):
#   - installs ci-entrypoint.sh root-owned at /usr/local/bin/ravencia-ci-deploy, so a
#     repository pull can never change what the key is allowed to run;
#   - with a public key line: creates the dedicated "deploy" login (no password), whose
#     root-owned authorized_keys runs nothing but that script, and whose only sudo right is
#     running it as ubuntu (/etc/sudoers.d/ravencia-deploy); adds deploy to sshd AllowUsers.
# Usage: install-ci-deploy.sh ["<ssh-ed25519 public key line>"]
# Without an argument only the entrypoint is refreshed. GitHub's DEPLOY_USER secret is "deploy".
# The key must not stay in ~ubuntu/.ssh/authorized_keys: remove that line after switching.

public_key="${1:-}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
entrypoint=/usr/local/bin/ravencia-ci-deploy
deploy_user=deploy
run_as="$(id -un)"
sudo -n install -o root -g root -m 0755 -- "$script_dir/ci-entrypoint.sh" "$entrypoint"
echo "Forced-command script refreshed: $entrypoint"
[[ -n "$public_key" ]] || exit 0

[[ "$public_key" =~ ^ssh-ed25519\ [A-Za-z0-9+/=]+(\ [^[:space:]\"]+)?$ ]] || { echo "Public key must be a single ssh-ed25519 line." >&2; exit 2; }

if ! getent passwd "$deploy_user" >/dev/null; then
  sudo -n useradd --create-home --shell /bin/sh --comment "Ravencia CI deploy (forced command)" "$deploy_user"
  echo "User $deploy_user created."
fi
sudo -n passwd --lock "$deploy_user" >/dev/null
deploy_home="$(getent passwd "$deploy_user" | cut -d: -f6)"

# Root-owned key file: the deploy login cannot add keys or lift the restriction itself.
sudo -n install -d -o root -g root -m 0755 "$deploy_home/.ssh"
printf 'restrict,command="%s" %s\n' "$entrypoint" "$public_key" | sudo -n tee "$deploy_home/.ssh/authorized_keys" >/dev/null
sudo -n chown root:root "$deploy_home/.ssh/authorized_keys"
sudo -n chmod 0644 "$deploy_home/.ssh/authorized_keys"

sudoers_tmp="$(mktemp)"
trap 'rm -f -- "$sudoers_tmp"' EXIT
printf '%s\n' \
  "# Installed by deploy/scripts/install-ci-deploy.sh: the CI deploy login may only run the" \
  "# root-owned deploy entrypoint, as the repository owner." \
  "$deploy_user ALL=($run_as) NOPASSWD: $entrypoint" > "$sudoers_tmp"
sudo -n visudo -cf "$sudoers_tmp" >/dev/null
sudo -n install -o root -g root -m 0440 -- "$sudoers_tmp" /etc/sudoers.d/ravencia-deploy

"$script_dir/sshd-allow-user.sh" "$deploy_user"
echo "Deploy key authorised for $deploy_user with a forced command; set DEPLOY_USER=$deploy_user in GitHub."
