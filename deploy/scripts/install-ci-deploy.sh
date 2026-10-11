#!/usr/bin/env bash
set -Eeuo pipefail

# One-time server setup: authorise the GitHub Actions deploy key so that it can run
# nothing except ci-entrypoint.sh. Usage: install-ci-deploy.sh "<ssh-ed25519 public key line>"

public_key="${1:?missing public key line}"
[[ "$public_key" =~ ^ssh-ed25519\ [A-Za-z0-9+/=]+(\ [^[:space:]\"]+)?$ ]] || { echo "Public key must be a single ssh-ed25519 line." >&2; exit 2; }
key_body="$(awk '{print $2}' <<<"$public_key")"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
entrypoint=/usr/local/bin/ravencia-ci-deploy
sudo -n install -o root -g root -m 0755 -- "$script_dir/ci-entrypoint.sh" "$entrypoint"

authorized_keys="$HOME/.ssh/authorized_keys"
install -d -m 700 -- "$HOME/.ssh"
touch -- "$authorized_keys"
chmod 600 -- "$authorized_keys"
if grep -qF -- "$key_body" "$authorized_keys"; then
  echo "Deploy key is already authorised; the forced-command script was refreshed."
else
  printf 'restrict,command="%s" %s\n' "$entrypoint" "$public_key" >> "$authorized_keys"
  echo "Deploy key authorised with a forced command."
fi
