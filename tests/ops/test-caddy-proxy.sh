#!/usr/bin/env bash
# deploy/caddy/Caddyfile(.production), text checks (no caddy binary needed; `caddy adapt` in Dockerfile.production checks
# the syntax when the edge image is built):
#   - every reverse_proxy to the API waits for a restarting API instead of answering 502 at once (VERITABANI-DAGITIM-4);
#   - the two files allow the same image hosts in the Content-Security-Policy (WEB-ALTYAPI-8).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

echo "caddy proxy and CSP"

# One line per reverse_proxy directive: "<line>|<settings of its block, space separated>"; "<line>|NO-BLOCK" when the
# directive has no block of its own.
proxy_blocks() {
  awk '
    !inside && /^[[:space:]]*reverse_proxy[[:space:]]/ {
      if ($0 !~ /\{[[:space:]]*$/) { print NR "|NO-BLOCK"; next }
      inside = 1; start = NR; settings = ""; match($0, /^[[:space:]]*/); indent = substr($0, 1, RLENGTH); next
    }
    inside && $0 == indent "}" { print start "|" settings; inside = 0; next }
    inside { line = $0; gsub(/^[[:space:]]+|[[:space:]]+$/, "", line); settings = settings " " line }
  ' "$1"
}

img_sources() {
  grep -o "img-src [^;]*" "$1" | tr ' ' '\n' | tail -n +2 | sort -u | tr '\n' ' '
}

for name in Caddyfile Caddyfile.production; do
  file="$repository_root/deploy/caddy/$name"
  blocks="$(proxy_blocks "$file")"
  assert_eq "2" "$(grep -c . <<<"$blocks")" "$name: two reverse_proxy blocks (auth and backend)"
  while IFS='|' read -r line settings; do
    assert_contains "$settings" "lb_try_duration 45s" "$name:$line reverse_proxy waits up to 45 s for a restarting API"
    assert_contains "$settings" "lb_try_interval 500ms" "$name:$line reverse_proxy retries every 500 ms"
  done <<<"$blocks"
  assert_eq "1" "$(grep -c "img-src " "$file")" "$name: one img-src list"
done

production_images="$(img_sources "$repository_root/deploy/caddy/Caddyfile.production")"
local_images="$(img_sources "$repository_root/deploy/caddy/Caddyfile")"
assert_eq "$production_images" "$local_images" "both Caddyfiles allow the same image sources"
for img_source in "'self'" "data:" "blob:"; do
  assert_contains "$production_images" "$img_source " "img-src keeps $img_source"
done
assert_not_contains "$production_images" "*" "img-src has no wildcard host"
assert_not_contains "$production_images" "https: " "img-src does not allow every https host"

finish "caddy proxy and CSP"
