#!/usr/bin/env bash
# VERITABANI-DAGITIM-4: the start-new-then-stop-old api release (deploy/scripts/api-rollover.sh). The script comes from the
# owner's prepared patch docs/patches/VERITABANI-DAGITIM-4-api-rollover.patch, applied to a scratch copy, until the patch
# is applied to the tree; then the tree's own copy is tested. docker and compose are shell functions over a state file
# (no docker, no chmod needed).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

echo "api rollover"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
patch_file="$repository_root/docs/patches/VERITABANI-DAGITIM-4-api-rollover.patch"
rollover="$repository_root/deploy/scripts/api-rollover.sh"
fast_deploy="$repository_root/deploy/scripts/fast-deploy.sh"
if [[ ! -f "$rollover" ]]; then
  scratch="$work/tree"
  mkdir -p -- "$scratch"
  git -C "$scratch" init --quiet
  # Copy every file the patch changes (new files come from the patch itself), then apply it there.
  while IFS= read -r path; do
    mkdir -p -- "$scratch/$(dirname -- "$path")"
    cp -- "$repository_root/$path" "$scratch/$path"
  done < <(sed -n 's#^--- a/##p' "$patch_file")
  git -C "$scratch" apply "$patch_file"
  rollover="$scratch/deploy/scripts/api-rollover.sh"
  fast_deploy="$scratch/deploy/scripts/fast-deploy.sh"
fi
# shellcheck source=/dev/null
source "$rollover"

# State: one "<id> <state>" line per api container ("running healthy", "running starting", "running unhealthy",
# "exited"); calls: every docker and compose call.
fake_containers="$work/containers"
calls="$work/calls"
export API_ROLLOVER_HEALTH_ATTEMPTS=3 API_ROLLOVER_HEALTH_INTERVAL=0 API_ROLLOVER_SETTLE=0
# shellcheck disable=SC2034 # read by api-rollover.sh
sudo_cmd=()
# shellcheck disable=SC2034 # read by api-rollover.sh
compose=(fake_compose)
new_state="running healthy"
compose_fails=false
next_id=2

# shellcheck disable=SC2317,SC2329 # called by api-rollover.sh through the compose array
fake_compose() {
  echo "compose $*" >> "$calls"
  [[ " $* " == *" --scale api="* ]] || { echo "api-1 running healthy" >> "$fake_containers"; return 0; }
  echo "api-$next_id $new_state" >> "$fake_containers"
  next_id=$((next_id + 1))
  [[ "$compose_fails" != true ]]
}

# shellcheck disable=SC2317,SC2329 # called by api-rollover.sh as "docker"
docker() {
  echo "docker $*" >> "$calls"
  local id
  case "$1" in
    ps) cut -d' ' -f1 "$fake_containers" ;;
    inspect) id="${*: -1}"; grep "^$id " "$fake_containers" | cut -d' ' -f2- ;;
    logs) ;;
    stop) ;;
    rm)
      for id in "$@"; do
        [[ "$id" == rm || "$id" == -f ]] && continue
        grep -v "^$id " "$fake_containers" > "$fake_containers.new" || true
        mv -- "$fake_containers.new" "$fake_containers"
      done ;;
    *) echo "unexpected docker call: $*" >&2; return 98 ;;
  esac
}

# run <state of the new container> [compose fails]: one rollover from a single healthy api-1.
run() {
  new_state="$1" compose_fails="${2:-false}" next_id=2
  echo "api-1 running healthy" > "$fake_containers"
  : > "$calls"
  local status=0
  api_rollover > "$work/output" 2>&1 || status=$?
  echo "$status"
}

status="$(run "running healthy")"
assert_eq "0" "$status" "a healthy new api completes the release"
assert_eq "api-2" "$(cut -d' ' -f1 "$fake_containers")" "only the new api is left"
assert_contains "$(cat "$calls")" "compose up -d --no-build --no-deps --no-recreate --scale api=2 api" \
  "the new api starts next to the running one, which is not recreated"
assert_contains "$(cat "$calls")" "docker stop --time 45 api-1" "the old api gets the 45 s grace"
inspect_line="$(grep -n "docker inspect" "$calls" | head -n 1 | cut -d: -f1)"
stop_line="$(grep -n "docker stop" "$calls" | cut -d: -f1)"
if (( inspect_line < stop_line )); then pass "the old api stops only after the new one is healthy"; else fail "the old api stops only after the new one is healthy"; fi

status="$(run "running unhealthy")"
assert_eq "1" "$status" "an unhealthy new api fails the release"
assert_eq "api-1" "$(cut -d' ' -f1 "$fake_containers")" "the old api keeps serving"
assert_not_contains "$(cat "$calls")" "docker stop" "the old api is never stopped"
assert_contains "$(cat "$calls")" "docker rm -f api-2" "the unhealthy new api is removed"

status="$(run "running starting")"
assert_eq "1" "$status" "a new api still starting after the wait fails the release"
assert_eq "api-1" "$(cut -d' ' -f1 "$fake_containers")" "the old api keeps serving after the wait"
assert_eq "3" "$(grep -c "docker inspect" "$calls")" "the health is asked API_ROLLOVER_HEALTH_ATTEMPTS times"

status="$(run "exited")"
assert_eq "1" "$status" "a new api that exited fails the release at once"
assert_eq "1" "$(grep -c "docker inspect" "$calls")" "an exited api is not waited for"

status="$(run "running healthy" true)"
assert_eq "1" "$status" "a failed compose scale-up fails the release"
assert_eq "api-1" "$(cut -d' ' -f1 "$fake_containers")" "a half-created api is removed and the old one serves"

: > "$fake_containers"
: > "$calls"
status=0
api_rollover > "$work/output" 2>&1 || status=$?
assert_eq "0" "$status" "with no api running the release starts one"
assert_contains "$(cat "$calls")" "compose up -d --no-build --no-deps api" "the ordinary start is used"
assert_not_contains "$(cat "$calls")" "--scale" "no scale-up without a running api"

deploy_text="$(cat "$fast_deploy")"
# shellcheck disable=SC2016 # the literal line of fast-deploy.sh
assert_contains "$deploy_text" 'source "$script_dir/api-rollover.sh"' "fast-deploy.sh loads the rollover"
assert_contains "$deploy_text" "api_rollover || release_status=\$?" "a full release replaces the api by the rollover"
assert_contains "$deploy_text" "release_services=(worker caddy)" "worker and caddy are recreated after the api"
assert_not_contains "$deploy_text" "release_services=(api worker caddy)" "the api is no longer recreated in place"

finish "api rollover"
