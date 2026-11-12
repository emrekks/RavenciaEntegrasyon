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
#   - parser whitelist ravencia/panel-whitelist (s02-enrich): the panel's own request shapes, never an IP;
#   - allowlists github-actions and ravencia-private, refreshed daily by
#     ravencia-crowdsec-allowlist.timer.
# Ending the simulation is the owner's manual step (docs/CI-CD-DEPLOY.md, "CrowdSec"), after reviewing the alerts:
#   bash deploy/scripts/install-crowdsec.sh --enforce
# turns global simulation off (bans reach nftables) and lets CrowdSec pull the community blocklist (the repository's
# config.yaml.local, which keeps it off, is no longer installed). A marker file keeps a later run without --enforce
# from putting the blocklist setting back; nothing in the deploy runs this script.

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
config_dir="$script_dir/../crowdsec"
unit_dir="$script_dir/../systemd"
keyring=/etc/apt/keyrings/crowdsec.gpg
suite="${RAVENCIA_CROWDSEC_SUITE:-noble}"
first_install=false
enforce=false
case "${1:-}" in
  --enforce) enforce=true ;;
  '') ;;
  *) echo "usage: install-crowdsec.sh [--enforce]" >&2; exit 2 ;;
esac
enforced_marker=/etc/crowdsec/ravencia-enforced

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
if [[ "$enforce" == true ]] || sudo -n test -e "$enforced_marker"; then
  # Enforcing: the package defaults pull the community blocklist and the subscribed blocklists.
  sudo -n rm -f /etc/crowdsec/config.yaml.local
else
  sudo -n install -m 0644 "$config_dir/config.yaml.local" /etc/crowdsec/config.yaml.local
fi
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
# The panel's own requests (API reads, SignalR, built assets, icons, SPA pages) never feed the HTTP scenarios; by
# request shape, not by IP. Simulation and --enforce runs alike; the reloads below pick it up.
sudo -n install -d -m 0755 /etc/crowdsec/parsers/s02-enrich
sudo -n install -m 0644 "$config_dir/ravencia-panel-whitelist.yaml" /etc/crowdsec/parsers/s02-enrich/ravencia-panel-whitelist.yaml

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
if [[ "$first_install" == true && "$enforce" != true ]]; then
  sudo -n cscli simulation enable --global
  echo "First installation: global simulation mode enabled (nothing is banned)."
fi
if [[ "$enforce" == true ]]; then
  sudo -n cscli simulation disable --global
  sudo -n touch "$enforced_marker"
  echo "Simulation ended: CrowdSec decisions are enforced by the nftables bouncer and the community blocklist is pulled."
fi

# The units run the root-owned copy in /usr/local/lib/ravencia that install-backup-timer.sh keeps current.
bash "$script_dir/install-backup-timer.sh" >/dev/null
sudo -n rm -f /usr/local/sbin/ravencia-crowdsec-allowlist
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
