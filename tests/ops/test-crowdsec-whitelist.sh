#!/usr/bin/env bash
# deploy/crowdsec/ravencia-panel-whitelist.yaml: the panel's own paths match a rule, scanner paths match none.
# The rules' regexes are RE2 written as POSIX ERE, so grep -E evaluates them as CrowdSec does (verb checks aside).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
whitelist="$repository_root/deploy/crowdsec/ravencia-panel-whitelist.yaml"

# Each "http_path matches '<regex>'" literal, with the expr string escape "\\" turned back into "\".
mapfile -t regexes < <(grep -oE "http_path matches '[^']+'" "$whitelist" | sed -E "s/^http_path matches '//; s/'\$//; s/\\\\\\\\/\\\\/g")
assert_eq 5 "${#regexes[@]}" "five path rules"

# The filter's User-Agent rule: a request without one, or from a scanner or a bare HTTP library, is never whitelisted.
agent_rule="$(grep -oE "http_user_agent matches '[^']+'" "$whitelist" | sed -E "s/^http_user_agent matches '//; s/'\$//; s/^\(\?i\)//")"
assert_contains "$(grep -E '^filter:' "$whitelist")" "!(evt.Parsed.http_user_agent in ['', '-'])" "an empty User-Agent is never whitelisted"
assert_contains "$(grep -E '^filter:' "$whitelist")" "!(evt.Parsed.http_user_agent matches '(?i)" "the scanner rule is a case-insensitive exclusion in the filter"
scanner_agents=(
  "sqlmap/1.8.2#stable (https://sqlmap.org)" "Mozilla/5.00 (Nikto/2.5.0) (Evasions:None) (Test:000003)"
  "Mozilla/5.0 (compatible; Nmap Scripting Engine; https://nmap.org/book/nse.html)" "masscan/1.3 (https://github.com/robertdavidgraham/masscan)"
  "Mozilla/5.0 zgrab/0.x" "python-requests/2.32.3" "Python-urllib/3.12" "Go-http-client/1.1" "curl/8.5.0" "Wget/1.21.4"
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 nuclei"
  "Fuzz Faster U Fool v2.1.0 ffuf" "WPScan v3.8.25 (https://wpscan.com/wordpress-security-scanner)" "libwww-perl/6.72"
)
for agent in "${scanner_agents[@]}"; do
  if printf '%s\n' "$agent" | grep -qiE -- "$agent_rule"; then pass "scanner agent stays visible: ${agent:0:40}"; else fail "scanner agent not excluded: $agent"; fi
done
browser_agents=(
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1"
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:132.0) Gecko/20100101 Firefox/132.0"
  "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36"
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0"
)
for agent in "${browser_agents[@]}"; do
  if printf '%s\n' "$agent" | grep -qiE -- "$agent_rule"; then fail "browser agent excluded: $agent"; else pass "browser agent may be whitelisted: ${agent:0:40}"; fi
done
for rule in "${regexes[@]}"; do
  status=0
  printf '' | grep -E -- "$rule" >/dev/null 2>&1 || status=$?
  if (( status < 2 )); then pass "valid ERE: ${rule:0:60}"; else fail "invalid ERE: $rule"; fi
done
assert_eq 5 "$(grep -cE "evt\.Meta\.http_verb in \[" "$whitelist")" "every rule is limited to named verbs"
assert_not_contains "$(cat "$whitelist")" "source_ip" "no rule is by IP"

whitelisted() {
  local path="$1" rule
  for rule in "${regexes[@]}"; do
    if printf '%s\n' "$path" | grep -qE -- "$rule"; then return 0; fi
  done
  return 1
}

panel=(
  /api/v1/products/01a1cb3e-b2c9-7ff0-a46d-064b479b831c
  "/api/v1/files/product-media/01a1cb3e-b2c9-7ff0-a46d-064b479b831c/content?redacted"
  /api/v1/products/01a1cb3e-b2c9-7ff0-a46d-064b479b831c/publication-status/01a1cb3e-b2c9-7ff0-a46d-064b479b831d
  /api/v1/auth/me
  "/api/v1/reports/payouts/lines?redacted"
  /api/v1/connections/01a1cb3e-b2c9-7ff0-a46d-064b479b831c/commission-tariffs/01a1cb3e-b2c9-7ff0-a46d-064b479b831d/rows
  /api/v1/jobs/metrics/lanes
  /api/connections
  "/hubs/operations?redacted"
  "/hubs/operations/negotiate?redacted"
  /assets/index-Bx3kLm9Q.js
  /assets/ProductEditor-a1B2c3D4.js.map
  /assets/index-Bx3kLm9Q.css
  /fonts/inter-latin-wght-normal.woff2
  /platforms/orders/trendyol.webp
  /pack/brand/ravencia-symbol-128.webp
  /favicon.ico
  /site.webmanifest
  /robots.txt
  /
  "/?redacted"
  /login
  /products/01a1cb3e-b2c9-7ff0-a46d-064b479b831c
  "/orders?redacted"
  /settings/security
)
for path in "${panel[@]}"; do
  if whitelisted "$path"; then pass "panel: $path"; else fail "panel path not whitelisted: $path"; fi
done

probes=(
  /.env /.git/config /.git/HEAD /wp-admin/ /wp-login.php /phpmyadmin/ /cgi-bin/luci /vendor/phpunit/phpunit/src/Util/PHP/eval-stdin.php
  /api/.env /api/v1/.env /api/v1/products/.env /api/v1/products/../../etc/passwd "/api/v1/products/1%27%20OR%201=1"
  "/api/v1/products/<script>" /api/v1/hooks/trendyol /api/v1/public/product-media/x/content /api/v1/admin /api/users
  /api/v1/imports/01a1cb3e/errors.csv /actuator/health /server-status /swagger/index.html /metrics /admin /config.json
  /backup.sql /assets/../.env /assets/.git/config /assets/app.js.bak /favicon.ico.bak /robots.txt.php /products.php
  /settings/.env /orders/../../etc/passwd /hubs/admin /composer.json /debug.log /.aws/credentials
)
for path in "${probes[@]}"; do
  if whitelisted "$path"; then fail "scanner path whitelisted: $path"; else pass "probe stays visible: $path"; fi
done

finish "crowdsec whitelist"
