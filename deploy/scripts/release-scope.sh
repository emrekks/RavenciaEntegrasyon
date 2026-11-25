#!/usr/bin/env bash
# Sourced by fast-deploy.sh (on the server), .claude/skills/ship/ship.sh (locally) and Validate's "Hash validated inputs"
# step (.github/workflows/ci.yml), so all three agree on which files feed the app image and the checks. Needs nothing
# but git (and sha256sum for the hashes).

# The one list of what the backend checks and the app image are built from (scan A-8): Validate hashes these paths to
# decide whether the backend jobs run, and web_only_change never counts a change under them as web-only. skills/ is in
# it because the build embeds skills/** as resources the tests read. The web app under src/ is left out of the hash
# (it has its own list below); this file is in both, so a change to a list re-runs the checks it governs.
backend_input_paths=(src tests skills global.json Directory.Build.props Directory.Packages.props MarketplaceHub.sln .editorconfig .config
  .github/workflows/ci.yml Dockerfile .dockerignore deploy/scripts/release-scope.sh)
# The frontend checks compare the generated API types with the API's contract file, so a change to it re-runs them.
frontend_input_paths=(src/MarketplaceHub.Web tests/MarketplaceHub.Application.Tests/ApiContract .github/workflows/ci.yml deploy/caddy .dockerignore
  deploy/scripts/release-scope.sh)

# The hash of the backend inputs at <rev> (HEAD by default), the web app left out.
backend_inputs_hash() {
  git ls-tree -r "${1:-HEAD}" -- "${backend_input_paths[@]}" \
    | awk -F'\t' '$2 !~ /^src\/MarketplaceHub\.Web\//' | sha256sum | cut -d' ' -f1
}

frontend_inputs_hash() {
  git ls-tree -r "${1:-HEAD}" -- "${frontend_input_paths[@]}" | sha256sum | cut -d' ' -f1
}

# True when <file> is one of the backend inputs (the web app under src/ excepted).
backend_input_file() {
  local file="$1" path
  [[ "$file" == src/MarketplaceHub.Web/* ]] && return 1
  for path in "${backend_input_paths[@]}"; do
    [[ "$file" == "$path" || "$file" == "$path"/* ]] && return 0
  done
  return 1
}

# True when <from>..<to> changes files and every changed file (both sides of a rename) is outside the app image and
# the compose/deploy plumbing: the web app, the edge image's own files, agent notes and docs. A backend input is never
# web-only, whatever its name.
web_only_change() {
  local from="$1" to="$2" files file
  files="$(git diff --no-renames --name-only "$from" "$to" --)" || return 1
  [[ -n "$files" ]] || return 1
  while IFS= read -r file; do
    backend_input_file "$file" && return 1
    case "$file" in
      src/MarketplaceHub.Web/*|deploy/caddy/*|.claude/*|docs/*) ;;
      */*) return 1 ;;
      *.md) ;;
      *) return 1 ;;
    esac
  done <<<"$files"
}
