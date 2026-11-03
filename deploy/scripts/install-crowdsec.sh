#!/usr/bin/env bash
set -Eeuo pipefail

# Installs or refreshes CrowdSec (security plan B1-B8) on the host. Run as ubuntu from the
# repository; idempotent. First installation starts in SIMULATION mode (B6): alerts are
# recorded, nothing is banned, and the community blocklist is not pulled.
#   - packages from CrowdSec's own apt repository (Ubuntu's archive carries 1.4.x); the
#     repository has no suite for this Ubuntu release, the noble build is used (static Go);
#   - acquisition from journald: sshd and the Caddy JSON access log;
#   - collections: linux, sshd, caddy, http-cve, base-http-scenarios, whitelists;
#   - firewall bouncer: nftables, IPv4 + IPv6, input and forward hooks;
#   - allowlists github-actions and ravencia-private, refreshed daily by
#     ravencia-crowdsec-allowlist.timer.
# Ending the simulation is a manual step (docs/CI-CD-DEPLOY.md, "CrowdSec").

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
config_dir="$script_dir/../crowdsec"
unit_dir="$script_dir/../systemd"
keyring=/etc/apt/keyrings/crowdsec.gpg
suite="${RAVENCIA_CROWDSEC_SUITE:-noble}"
first_install=false

if [[ ! -s "$keyring" ]]; then
  key_tmp="$(mktemp)"
  curl --silent --show-error --fail --location https://packagecloud.io/crowdsec/crowdsec/gpgkey -o "$key_tmp"
  sudo -n install -d -m 0755 /etc/apt/keyrings
  sudo -n gpg --batch --yes --dearmor -o "$keyring" "$key_tmp"
  rm -f -- "$key_tmp"
  sudo -n chmod 0644 "$keyring"
fi
printf 'deb [signed-by=%s] https://packagecloud.io/crowdsec/crowdsec/ubuntu/ %s main\n' "$keyring" "$suite" \
  | sudo -n tee /etc/apt/sources.list.d/crowdsec.list >/dev/null

if ! dpkg-query -W -f '${Status}' crowdsec 2>/dev/null | grep -q 'install ok installed'; then
  first_install=true
fi
sudo -n apt-get -o DPkg::Lock::Timeout=600 update -q
# The package starts the engine at once, so the simulation switch and the blocklist setting
# are in place before it: nothing can be enforced between installation and configuration.
sudo -n install -d -m 0755 /etc/crowdsec
sudo -n install -m 0644 "$config_dir/config.yaml.local" /etc/crowdsec/config.yaml.local
if [[ "$first_install" == true ]] && ! sudo -n test -e /etc/crowdsec/simulation.yaml; then
  printf 'simulation: true\nexclusions: []\n' | sudo -n tee /etc/crowdsec/simulation.yaml >/dev/null
fi
sudo -n env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=l apt-get -o DPkg::Lock::Timeout=600 \
  -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold -y install crowdsec crowdsec-firewall-bouncer-nftables jq

sudo -n install -d -m 0755 /etc/crowdsec/acquis.d
sudo -n install -m 0644 "$config_dir/acquis-ravencia.yaml" /etc/crowdsec/acquis.d/ravencia.yaml
# The package's auto-setup reads /var/log/auth.log and /var/log/syslog (rsyslog copies of the
# same journal): sshd would be counted twice. Journald is the only source.
sudo -n rm -f /etc/crowdsec/acquis.d/setup.sshd.yaml /etc/crowdsec/acquis.d/setup.linux.yaml
sudo -n install -m 0600 "$config_dir/crowdsec-firewall-bouncer.yaml.local" /etc/crowdsec/bouncers/crowdsec-firewall-bouncer.yaml.local

sudo -n cscli hub update >/dev/null
sudo -n cscli collections install crowdsecurity/linux crowdsecurity/sshd crowdsecurity/caddy \
  crowdsecurity/http-cve crowdsecurity/base-http-scenarios >/dev/null
sudo -n cscli parsers install crowdsecurity/whitelists >/dev/null
sudo -n systemctl reload-or-restart crowdsec
# The bouncer's postinst registers its API key only when the engine already answers; in the
# same apt run it may not (the file then holds "<API_KEY>"). The key is never printed.
if ! sudo -n cscli bouncers list -o json | jq -e 'length > 0' >/dev/null; then
  bouncer_key="$(sudo -n cscli bouncers add "crowdsec-firewall-bouncer-$(date +%s)" -o raw)"
  [[ "$bouncer_key" =~ ^[A-Za-z0-9+/=_-]+$ ]] || { echo "Unexpected bouncer key format." >&2; exit 1; }
  sudo -n sed -i -E "s|^api_key:.*$|api_key: ${bouncer_key}|" /etc/crowdsec/bouncers/crowdsec-firewall-bouncer.yaml
  unset bouncer_key
  echo "Firewall bouncer registered."
fi
if [[ "$first_install" == true ]]; then
  sudo -n cscli simulation enable --global
  echo "First installation: global simulation mode enabled (nothing is banned)."
fi

sudo -n install -o root -g root -m 0755 -- "$script_dir/crowdsec-allowlist-refresh.sh" /usr/local/sbin/ravencia-crowdsec-allowlist
for unit in ravencia-crowdsec-allowlist.service ravencia-crowdsec-allowlist.timer; do
  sudo -n install -m 0644 "$unit_dir/$unit" "/etc/systemd/system/$unit"
done
sudo -n systemctl daemon-reload
sudo -n systemctl reload-or-restart crowdsec
sudo -n systemctl restart crowdsec-firewall-bouncer
sudo -n systemctl enable --now ravencia-crowdsec-allowlist.timer
sudo -n systemctl start ravencia-crowdsec-allowlist.service
sudo -n cscli simulation status
sudo -n cscli bouncers list
