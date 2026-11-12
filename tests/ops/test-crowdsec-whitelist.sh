#!/usr/bin/env bash
# deploy/crowdsec/ravencia-panel-whitelist.yaml: the panel's own paths match a rule, scanner paths match none.
# The rules' regexes are RE2 written as POSIX ERE, so grep -E evaluates them as CrowdSec does (verb checks aside).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
whitelist="$repository_root/deploy/crowdsec/ravencia-panel-whitelist.yaml"

# Each "matches '<regex>'" literal, with the expr string escape "\\" turned back into "\".
mapfile -t regexes < <(grep -oE "matches '[^']+'" "$whitelist" | sed -E "s/^matches '//; s/'\$//; s/\\\\\\\\/\\\\/g")
assert_eq 5 "${#regexes[@]}" "five path rules"
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
