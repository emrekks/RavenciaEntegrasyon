#!/usr/bin/env bash
# The web-only release of deploy/scripts/fast-deploy.sh: which releases count as web-only
# (deploy/scripts/release-scope.sh), and a stubbed run of fast-deploy.sh against a throwaway repository whose
# docker, sudo and curl are fakes that keep the containers' images, image IDs and Compose hashes in files.
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"
# shellcheck source=deploy/scripts/release-scope.sh
source "$repository_root/deploy/scripts/release-scope.sh"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
export GIT_AUTHOR_NAME=ops-test GIT_AUTHOR_EMAIL=ops-test@example.invalid
export GIT_COMMITTER_NAME=ops-test GIT_COMMITTER_EMAIL=ops-test@example.invalid
export GIT_CONFIG_NOSYSTEM=1

repo="$work/repo"
git init --quiet --initial-branch=main "$repo"
mkdir -p -- "$repo/deploy/scripts" "$repo/deploy/compose" "$repo/deploy/caddy" "$repo/src/MarketplaceHub.Web/src" \
  "$repo/src/MarketplaceHub.Api" "$repo/docs"
cp -- "$repository_root"/deploy/scripts/{fast-deploy.sh,lib.sh,release-scope.sh} "$repo/deploy/scripts/"
: > "$repo/deploy/compose/compose.yaml"
: > "$repo/deploy/compose/compose.production.yaml"
echo web > "$repo/src/MarketplaceHub.Web/src/app.ts"
echo api > "$repo/src/MarketplaceHub.Api/Program.cs"
echo deploy/secrets/ > "$repo/.gitignore"
commit() {
  git -C "$repo" add --all
  git -C "$repo" commit --quiet -m "$1"
  git -C "$repo" rev-parse HEAD
}
change() { printf '%s\n' "$2" >> "$repo/$1"; commit "$1"; }
rev_base="$(commit base)"

# scope_is <web-only|full> <from> <to> <name>
scope_is() {
  local actual=full
  if web_only_change "$2" "$3"; then actual=web-only; fi
  assert_eq "$1" "$actual" "$4"
}

echo "release scope"
git -C "$repo" checkout --quiet -b scope
cd "$repo"
web_rev="$(change src/MarketplaceHub.Web/src/app.ts 1)"
scope_is web-only "$rev_base" "$web_rev" "a web app change is web-only"
md_rev="$(change CLAUDE.md note)"
docs_rev="$(change docs/x.md note)"
caddy_rev="$(change deploy/caddy/Caddyfile.production x)"
scope_is web-only "$rev_base" "$caddy_rev" "web, root *.md, docs and edge files together are web-only"
api_rev="$(change src/MarketplaceHub.Api/Program.cs 1)"
scope_is full "$caddy_rev" "$api_rev" "an API change is not web-only"
scope_is full "$rev_base" "$api_rev" "a range with one API change is not web-only"
script_rev="$(change deploy/scripts/lib.sh '# x')"
scope_is full "$api_rev" "$script_rev" "a deploy script change is not web-only"
nested_md_rev="$(change src/MarketplaceHub.Api/README.md x)"
scope_is full "$script_rev" "$nested_md_rev" "a *.md outside the web paths is not web-only"
git mv src/MarketplaceHub.Api/Program.cs src/MarketplaceHub.Web/src/Program.cs
rename_rev="$(commit rename)"
scope_is full "$nested_md_rev" "$rename_rev" "a rename out of the API is not web-only"
scope_is full "$web_rev" "$web_rev" "an empty range is not web-only"
: "$md_rev" "$docs_rev"

# Scan A-8: one input list. A change under skills/ alone (embedded in the build, read by the tests) changes the backend
# hash Validate keys its backend jobs on and is not a web-only release; a web change leaves the backend hash alone.
# Every backend input, whatever its name, is never web-only.
skills_base="$(git -C "$repo" rev-parse HEAD)"
mkdir -p -- "$repo/skills/demo"
skills_rev="$(change skills/demo/SKILL.md 'a skill')"
assert_eq true "$([[ "$(backend_inputs_hash "$skills_base")" != "$(backend_inputs_hash "$skills_rev")" ]] && echo true || echo false)" \
  "a skills/-only change changes the backend hash"
scope_is full "$skills_base" "$skills_rev" "a skills/-only change is not web-only"
web_only_rev="$(change src/MarketplaceHub.Web/src/app.ts 2)"
assert_eq "$(backend_inputs_hash "$skills_rev")" "$(backend_inputs_hash "$web_only_rev")" "a web change leaves the backend hash alone"
assert_eq true "$([[ "$(frontend_inputs_hash "$skills_rev")" != "$(frontend_inputs_hash "$web_only_rev")" ]] && echo true || echo false)" \
  "a web change changes the frontend hash"
# The two lists agree: no backend input lies under a path the web-only release skips, and the web app is not one.
for input in "${backend_input_paths[@]}"; do
  assert_eq true "$(backend_input_file "$input" && echo true || echo false)" "$input is a backend input"
  case "$input" in
    src/MarketplaceHub.Web|src/MarketplaceHub.Web/*|deploy/caddy|deploy/caddy/*|.claude|.claude/*|docs|docs/*) fail "$input is also a web-only path" ;;
    */*) ;;
    *.md) fail "$input is also a web-only path" ;;
  esac
done
assert_eq false "$(backend_input_file src/MarketplaceHub.Web/src/app.ts && echo true || echo false)" "the web app is not a backend input"
# Validate's hash step reads the same functions instead of a list of its own.
ci_hash_step="$(cat "$repository_root/.github/workflows/ci.yml")"
assert_contains "$ci_hash_step" 'source deploy/scripts/release-scope.sh' "ci.yml sources release-scope.sh"
# shellcheck disable=SC2016 # the literal lines of ci.yml
assert_contains "$ci_hash_step" 'backend="$(backend_inputs_hash HEAD)"' "ci.yml hashes the backend inputs of release-scope.sh"
# shellcheck disable=SC2016 # the literal lines of ci.yml
assert_contains "$ci_hash_step" 'frontend="$(frontend_inputs_hash HEAD)"' "ci.yml hashes the frontend inputs of release-scope.sh"
assert_not_contains "$ci_hash_step" 'git ls-tree -r HEAD --' "ci.yml keeps no input list of its own"
cd - >/dev/null
git -C "$repo" checkout --quiet main

# ---- Stubbed fast-deploy.sh ------------------------------------------------------------------------------------
mkdir -p -- "$repo/deploy/secrets"
for name in postgres_password.txt app_db_connection.txt credential_key.txt dp_certificate.pfx dp_certificate_password.txt \
  production.env alert_sync_token.txt alert_webhook_url.txt; do
  echo x > "$repo/deploy/secrets/$name"
done
echo 'x;Maximum Pool Size=25' > "$repo/deploy/secrets/app_db_connection.txt"

bin="$work/bin"
state="$work/docker"
mkdir -p -- "$bin" "$state"
cat > "$bin/sudo" <<'STUB'
#!/usr/bin/env bash
[[ "${1:-}" != -n ]] || shift
exec "$@"
STUB
cat > "$bin/curl" <<'STUB'
#!/usr/bin/env bash
for arg in "$@"; do
  case "$arg" in
    */health/ready) printf 200; exit 0 ;;
  esac
done
last="${*: -1}"
[[ "$last" == */ ]] && printf '<div id="root"></div><script src="/assets/a.js"></script>'
exit 0
STUB
# State: <service>.image (tag the container was created from), <service>.id, <service>.hash (config-hash label),
# tags (tag=id lines), compose-hash.<service> (what `config --hash` prints for the image the env names).
cat > "$bin/docker" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
s="$FAKE_DOCKER_STATE"
echo "docker $*" >> "$s/calls"
tag_id() { awk -F= -v t="$1" '$1 == t { print $2 }' "$s/tags" | tail -n 1; }
set_tag() { echo "$1=$2" >> "$s/tags"; }
if [[ "$1" == compose ]]; then
  args=" $* "
  if [[ "$args" == *" config --hash "* ]]; then
    service="${*: -1}"
    expected="$(cat "$s/compose-hash.$service")"
    # The hash covers the image: only the image the running container names gives its label back.
    [[ "$MARKETPLACEHUB_APP_IMAGE" == "$(cat "$s/$service.image")" ]] || expected="other-image-hash"
    echo "$service $expected"; exit 0
  fi
  [[ "$args" != *" config "* ]] || exit 0
  if [[ "$args" == *" ps -q "* ]]; then echo "cid-${*: -1}"; exit 0; fi
  if [[ "$args" == *" up -d "* ]]; then
    for service in api worker caddy; do
      [[ "$args" == *" $service "* ]] || continue
      image="$MARKETPLACEHUB_APP_IMAGE"
      [[ "$service" != caddy ]] || image="$MARKETPLACEHUB_EDGE_IMAGE"
      echo "$image" > "$s/$service.image"
      tag_id "$image" > "$s/$service.id"
      echo "recreated-$service" > "$s/$service.hash"
    done
    exit 0
  fi
  echo "unexpected compose call: $*" >&2; exit 98
fi
case "$1" in
  ps) for a in "$@"; do [[ "$a" != label=com.docker.compose.service=* ]] || echo "cid-${a##*=}"; done ;;
  inspect)
    format="$3" service="${4#cid-}"
    case "$format" in
      '{{.Config.Image}}') cat "$s/$service.image" ;;
      '{{.Image}}') cat "$s/$service.id" ;;
      *config-hash*) cat "$s/$service.hash" ;;
      *Health*) echo healthy ;;
      *) echo "unexpected inspect: $*" >&2; exit 98 ;;
    esac ;;
  image)
    case "$2" in
      inspect) id="$(tag_id "${*: -1}")"; [[ -n "$id" ]] || exit 1; echo "$id" ;;
      ls) cut -d= -f1 "$s/tags" | sort -u ;;
      rm) grep -v "^$3=" "$s/tags" > "$s/tags.new" || true; mv "$s/tags.new" "$s/tags" ;;
      prune) ;;
    esac ;;
  tag) set_tag "$3" "$(tag_id "$2")" ;;
  build)
    tag="" file=""
    while (($#)); do case "$1" in -t) tag="$2"; shift 2 ;; -f) file="$2"; shift 2 ;; *) shift ;; esac; done
    [[ "$file" != Dockerfile || -z "${FAKE_FAIL_APP_BUILD:-}" ]] || { echo "app build stopped by the test"; exit 99; }
    set_tag "$tag" "sha256:built-${tag##*:}" ;;
  builder) ;;
  *) echo "unexpected docker call: $*" >&2; exit 98 ;;
esac
STUB
chmod +x "$bin"/*

# A stack serving release <rev>: api/worker from app manual-<rev>, caddy from edge manual-<rev>.
serve() {
  local short="${1:0:12}"
  printf '%s\n' "marketplacehub-app:manual-$short=sha256:app-$short" "marketplacehub-edge:manual-$short=sha256:edge-$short" > "$state/tags"
  for service in api worker; do
    echo "marketplacehub-app:manual-$short" > "$state/$service.image"
    echo "sha256:app-$short" > "$state/$service.id"
    echo "hash-$service" > "$state/$service.hash"
    echo "hash-$service" > "$state/compose-hash.$service"
  done
  echo "marketplacehub-edge:manual-$short" > "$state/caddy.image"
  echo "sha256:edge-$short" > "$state/caddy.id"
  echo "hash-caddy" > "$state/caddy.hash"
}
run_deploy() {
  local status=0 output
  : > "$state/calls"
  git -C "$repo" checkout --quiet "$1"
  output="$(PATH="$bin:$PATH" FAKE_DOCKER_STATE="$state" DEPLOY_REVISION="$1" RAVENCIA_DEPLOY_STATE_DIR="$work/deploy-logs" \
    MARKETPLACEHUB_SITE_ADDRESS=https://panel.invalid bash "$repo/deploy/scripts/fast-deploy.sh" 2>&1)" || status=$?
  printf '%s\nexit=%s\n' "$output" "$status"
}

echo "fast-deploy web-only release"
git -C "$repo" checkout --quiet main
rev_one="$(change src/MarketplaceHub.Web/src/app.ts one)"
serve "$rev_base"
result="$(run_deploy "$rev_one")"
calls="$(cat "$state/calls")"
assert_contains "$result" "Release scope: web-only." "a web-only range takes the web-only path"
assert_contains "$result" "exit=0" "the web-only release finishes"
assert_contains "$calls" "docker tag marketplacehub-app:manual-${rev_base:0:12} marketplacehub-app:manual-${rev_one:0:12}" \
  "tags the serving app image with the new revision"
assert_not_contains "$calls" "-f Dockerfile ." "does not build the app image"
assert_contains "$calls" "-f deploy/caddy/Dockerfile.production" "builds the edge image"
assert_contains "$calls" "up -d --no-build --no-deps caddy" "recreates caddy alone"
assert_not_contains "$calls" "postgres migrate" "runs no migration"
assert_not_contains "$calls" "BACKUP_FORCE" "takes no backup"
assert_eq "marketplacehub-app:manual-${rev_base:0:12}" "$(cat "$state/api.image")" "api keeps its container"
assert_eq "hash-worker" "$(cat "$state/worker.hash")" "worker keeps its container"
assert_eq "marketplacehub-edge:manual-${rev_one:0:12}" "$(cat "$state/caddy.image")" "caddy runs the new edge image"
assert_contains "$(cat "$work/deploy-logs/releases.tsv")" "deploy-web	$rev_one" "records a deploy-web release"
assert_eq "$rev_one" "$(cat "$work/deploy-logs/.last-success")" "records the verified revision"

rev_two="$(change src/MarketplaceHub.Web/src/app.ts two)"
result="$(run_deploy "$rev_two")"
calls="$(cat "$state/calls")"
assert_contains "$result" "Release scope: web-only." "a second web-only release follows the first"
assert_contains "$calls" "docker tag marketplacehub-app:manual-${rev_one:0:12} marketplacehub-app:manual-${rev_two:0:12}" \
  "names the serving release from caddy and the image ID, not the api tag"
tags="$(cut -d= -f1 "$state/tags")"
assert_contains "$tags" "marketplacehub-app:manual-${rev_one:0:12}" "keeps the rollback pair's app tag"
assert_contains "$tags" "marketplacehub-edge:manual-${rev_one:0:12}" "keeps the rollback pair's edge tag"
assert_contains "$tags" "marketplacehub-app:manual-${rev_base:0:12}" "keeps the tag the api container names"
assert_not_contains "$tags" "marketplacehub-edge:manual-${rev_base:0:12}" "prunes older edge tags"

echo "fast-deploy falls back to the full path"
rev_api="$(change src/MarketplaceHub.Api/Program.cs api)"
result="$(FAKE_FAIL_APP_BUILD=1 run_deploy "$rev_api")"
assert_contains "$result" "Release scope: full (files outside the web app changed since ${rev_two:0:12})." "an API change takes the full path"
assert_contains "$result" "app build stopped by the test" "the full path builds the app image"

serve "$rev_api"
rev_web="$(change src/MarketplaceHub.Web/src/app.ts three)"
echo "changed" > "$state/compose-hash.api"
result="$(FAKE_FAIL_APP_BUILD=1 run_deploy "$rev_web")"
assert_contains "$result" "Release scope: full (api Compose configuration changed or unreadable)." \
  "a changed api Compose configuration (production.env) takes the full path"

serve "$rev_api"
echo "marketplacehub-app:manual-${rev_api:0:12}=sha256:rebuilt" >> "$state/tags"
result="$(FAKE_FAIL_APP_BUILD=1 run_deploy "$rev_web")"
assert_contains "$result" "Release scope: full (serving release unknown" "an app tag that is not the running image takes the full path"

serve "$rev_api"
result="$(FAKE_FAIL_APP_BUILD=1 RAVENCIA_DEPLOY_MODE=full run_deploy "$rev_web")"
assert_contains "$result" "Release scope: full (RAVENCIA_DEPLOY_MODE=full)." "RAVENCIA_DEPLOY_MODE=full forces the full path"

serve "$rev_web"
result="$(FAKE_FAIL_APP_BUILD=1 run_deploy "$rev_web")"
assert_contains "$result" "Release scope: full (redeploy of the serving revision)." "a redeploy is full"

finish "fast-deploy web-only"
