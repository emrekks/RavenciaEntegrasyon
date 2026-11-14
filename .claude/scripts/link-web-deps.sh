#!/usr/bin/env bash
# Gives this worktree's src/MarketplaceHub.Web/node_modules without an npm ci of its own: a Windows directory
# junction to an install of the same package-lock.json. The main checkout's node_modules is used when its lockfile
# is the same and its install is complete; otherwise a shared install keyed by the lockfile's hash
# (%LOCALAPPDATA%/ravencia-web-deps/<hash>, outside OneDrive; RAVENCIA_WEB_DEPS_CACHE overrides) is used, made once
# with npm ci by the first worktree that needs it. Any failure falls back to a plain npm ci here and says why.
# Usage, from anywhere inside the worktree: bash .claude/scripts/link-web-deps.sh
# Removing the worktree: `git worktree remove` and `rm -rf` unlink the junction and leave its target alone (checked
# 15 Nov 2026; git leaves the empty junction folder behind, `rm -rf` it). PowerShell 5.1 `Remove-Item -Recurse`
# follows junctions: drop the junction first with cmd /c rmdir src\MarketplaceHub.Web\node_modules.
set -Eeuo pipefail

top="$(git rev-parse --show-toplevel)"
web="$top/src/MarketplaceHub.Web"
nm="$web/node_modules"
lock="$web/package-lock.json"
main_top="$(dirname -- "$(git rev-parse --path-format=absolute --git-common-dir)")"
hash="$(sha256sum "$lock" | cut -c1-16)"
cache_root="${RAVENCIA_WEB_DEPS_CACHE:-$(cygpath -u "${LOCALAPPDATA:-$HOME/AppData/Local}")/ravencia-web-deps}"
cache="$cache_root/$hash"

winpath() { cygpath -w "$1"; }
is_junction() { [[ -L "$1" ]] || { [[ -d "$1" ]] && fsutil reparsepoint query "$(winpath "$1")" >/dev/null 2>&1; }; }
drop_junction() { MSYS2_ARG_CONV_EXCL="*" cmd /c rmdir "$(winpath "$1")" >/dev/null; }

# Every package the lockfile installs on this machine (optional ones may be skipped for another OS/CPU) is present.
complete() {
  node -e '
    const fs = require("fs"), path = require("path");
    const [lockFile, dir] = process.argv.slice(1);
    const pkgs = JSON.parse(fs.readFileSync(lockFile, "utf8")).packages || {};
    const missing = Object.entries(pkgs).filter(([k, e]) => k.startsWith("node_modules/") && !e.optional && !e.link
      && !fs.existsSync(path.join(dir, k.slice("node_modules/".length), "package.json")));
    if (missing.length) { console.error("missing " + missing.length + " packages, e.g. " + missing[0][0]); process.exit(1); }
  ' "$1" "$2"
}

fallback() {
  echo "link-web-deps: $1; running npm ci in this worktree instead."
  if is_junction "$nm"; then drop_junction "$nm"; fi
  (cd "$web" && npm ci --no-audit --no-fund)
  exit 0
}

[[ "$top" != "$main_top" ]] || { echo "link-web-deps: this is the main checkout; run npm ci there instead."; exit 1; }

if [[ -e "$nm" ]] && ! is_junction "$nm"; then
  if complete "$lock" "$nm" 2>/dev/null; then echo "link-web-deps: a complete node_modules of its own is already here; kept."; exit 0; fi
  echo "link-web-deps: removing this worktree's own incomplete node_modules."
  rm -rf -- "$nm"
fi

target=""
if cmp -s "$lock" "$main_top/src/MarketplaceHub.Web/package-lock.json" \
  && complete "$lock" "$main_top/src/MarketplaceHub.Web/node_modules" 2>/dev/null; then
  target="$main_top/src/MarketplaceHub.Web/node_modules"
else
  if ! complete "$lock" "$cache/node_modules" 2>/dev/null; then
    mkdir -p -- "$cache_root"
    # One installer per lockfile; the others wait (a lock older than 20 minutes is taken over).
    waited=0
    until mkdir "$cache.lock" 2>/dev/null; do
      if [[ -n "$(find "$cache.lock" -maxdepth 0 -mmin +20 2>/dev/null)" ]]; then rm -rf -- "$cache.lock"; continue; fi
      (( waited % 60 )) || echo "link-web-deps: another worktree is installing $hash; waiting."
      sleep 5; waited=$((waited + 5))
    done
    trap 'rm -rf -- "$cache.lock"' EXIT
    if ! complete "$lock" "$cache/node_modules" 2>/dev/null; then
      echo "link-web-deps: no shared install of this lockfile yet; npm ci into $cache (once per lockfile)."
      tmp="$cache.tmp.$$"
      rm -rf -- "$tmp" "$cache"
      mkdir -p -- "$tmp"
      cp -- "$web/package.json" "$lock" "$tmp/"
      (cd "$tmp" && npm ci --no-audit --no-fund) || { rm -rf -- "$tmp"; fallback "npm ci of the shared install failed"; }
      mv -- "$tmp" "$cache"
    fi
    rm -rf -- "$cache.lock"
    trap - EXIT
  fi
  target="$cache/node_modules"
fi

if is_junction "$nm"; then drop_junction "$nm"; fi
MSYS2_ARG_CONV_EXCL="*" cmd /c mklink /J "$(winpath "$nm")" "$(winpath "$target")" >/dev/null || fallback "mklink /J failed"
complete "$lock" "$nm" || fallback "the linked node_modules is incomplete"
echo "link-web-deps: node_modules -> $(winpath "$target")"
