#!/usr/bin/env bash
# deploy/caddy/Caddyfile(.production): /assets/* is served by its own handle without the SPA fallback, so a chunk an
# older release had is a plain 404 (no-store), never index.html under the one-year immutable header (WEB-ALTYAPI-3).
# A text check of both files (no caddy binary needed); `caddy adapt` in Dockerfile.production checks the syntax.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

# The body of the first "handle <matcher> {" block of a file, up to its closing brace at the same indentation.
handle_block() {
  local file="$1" opener="$2"
  awk -v opener="$opener" '
    !inside && $0 ~ "^[[:space:]]*" opener "[[:space:]]*\\{[[:space:]]*$" { inside = 1; match($0, /^[[:space:]]*/); indent = substr($0, 1, RLENGTH); next }
    inside && $0 == indent "}" { exit }
    inside { print }
  ' "$file"
}

for name in Caddyfile Caddyfile.production; do
  file="$repository_root/deploy/caddy/$name"
  assets="$(handle_block "$file" 'handle /assets/[*]')"
  shell="$(handle_block "$file" 'handle')"
  assert_contains "$assets" 'root * /srv' "$name: /assets has its own handle on /srv"
  assert_not_contains "$assets" 'try_files' "$name: /assets has no SPA fallback (a missing chunk is a 404)"
  assert_contains "$assets" '@asset_found file' "$name: the immutable header is limited to files that exist"
  assert_contains "$assets" 'header @asset_found Cache-Control "public, max-age=31536000, immutable"' "$name: an existing asset is cached for a year"
  assert_contains "$assets" 'header @asset_missing Cache-Control "no-store"' "$name: a missing asset is not cached"
  assert_contains "$assets" 'precompressed br zstd' "$name: the .br/.zst copies are still served"
  # A missing file must end in file_server's own 404: nothing in the block may hand the request on or answer otherwise.
  for passer in pass_thru rewrite redir respond reverse_proxy index.html; do
    assert_not_contains "$assets" "$passer" "$name: a missing asset is not passed on or rewritten ($passer)"
  done
  assert_eq "1" "$(grep -c 'file_server' <<<"$assets")" "$name: /assets ends in one file_server"
  assert_contains "$shell" 'try_files {path} /index.html' "$name: application paths keep the SPA fallback"
  assert_not_contains "$shell" 'immutable' "$name: the SPA fallback never sends the immutable header"
done

finish "caddy assets"
