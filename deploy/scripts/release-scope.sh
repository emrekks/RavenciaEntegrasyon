#!/usr/bin/env bash
# Sourced by fast-deploy.sh (on the server) and .claude/skills/ship/ship.sh (locally), so both agree on which
# releases only change the edge image. Needs nothing but git.

# True when <from>..<to> changes files and every changed file (both sides of a rename) is outside the app image and
# the compose/deploy plumbing: the web app, the edge image's own files, agent notes and docs.
web_only_change() {
  local from="$1" to="$2" files file
  files="$(git diff --no-renames --name-only "$from" "$to" --)" || return 1
  [[ -n "$files" ]] || return 1
  while IFS= read -r file; do
    case "$file" in
      src/MarketplaceHub.Web/*|deploy/caddy/*|.claude/*|docs/*) ;;
      */*) return 1 ;;
      *.md) ;;
      *) return 1 ;;
    esac
  done <<<"$files"
}
