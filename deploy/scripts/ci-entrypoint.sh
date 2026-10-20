#!/usr/bin/env bash
set -Eeuo pipefail

# Forced command for the GitHub Actions deploy key. It is installed root-owned at
# /usr/local/bin/ravencia-ci-deploy by install-ci-deploy.sh, so a repository pull
# can never change what the key is allowed to run. The only accepted input is the
# 40-character commit SHA that passed the Validate workflow, optionally followed
# by the word "redeploy" to rebuild a revision that is already live.
#
# State (written by fast-deploy.sh / rollback.sh, see deploy/scripts/lib.sh):
#   ~/deploy-logs/.last-success  full SHA of the last verified deploy
#   ~/deploy-logs/.hold          SHA that was rolled back; it and its ancestors are
#                                refused until a newer commit is deployed

# A dropped SSH session must not interrupt a deployment in the middle of a migration.
trap '' HUP

request="${SSH_ORIGINAL_COMMAND:-}"
[[ "$request" =~ ^([0-9a-f]{40})(\ redeploy)?$ ]] || { echo "Usage: ssh <host> '<40-character commit SHA> [redeploy]'" >&2; exit 2; }
revision="${BASH_REMATCH[1]}"
redeploy=false
[[ -z "${BASH_REMATCH[2]}" ]] || redeploy=true

repository_root="${RAVENCIA_REPOSITORY_ROOT:-$HOME/RavenciaEntegrasyon}"
log_dir="$HOME/deploy-logs"
mkdir -p -- "$log_dir"
last_success_file="$log_dir/.last-success"
hold_file="$log_dir/.hold"

# The deployment child inherits this descriptor, so the lock outlives the SSH session.
exec 9>"$log_dir/.lock"
flock -w 3600 9 || { echo "Another deployment is still running." >&2; exit 75; }

cd "$repository_root"
git fetch --quiet origin main
git cat-file -e "$revision^{commit}" 2>/dev/null || { echo "Revision $revision is not on origin/main." >&2; exit 1; }

is_ancestor_or_equal() { git merge-base --is-ancestor "$1" "$2" 2>/dev/null; }

hold=""
[[ ! -s "$hold_file" ]] || hold="$(head -c 40 -- "$hold_file")"
if [[ -n "$hold" ]] && is_ancestor_or_equal "$revision" "$hold"; then
  # "::warning::" is shown as an annotation on the GitHub Actions run.
  echo "::warning::Revision $revision is held after a rollback of $hold; push a newer commit (or remove $hold_file) to deploy again."
  exit 0
fi

last_success=""
[[ ! -s "$last_success_file" ]] || last_success="$(head -c 40 -- "$last_success_file")"
if [[ "$redeploy" == false && -n "$last_success" ]] && is_ancestor_or_equal "$revision" "$last_success"; then
  echo "Revision $revision is already live: the last verified deploy ($last_success) contains it."
  exit 0
fi

if [[ "$(git rev-parse HEAD)" != "$revision" ]]; then
  if is_ancestor_or_equal "$revision" HEAD; then
    if [[ -z "$last_success" ]]; then
      # No record yet (first run after this script was installed): keep the old rule.
      echo "Revision $revision is already contained in the checked-out history (no .last-success record yet)."
      exit 0
    fi
    echo "Revision $revision is older than the checked-out $(git rev-parse --short=12 HEAD), whose deploy did not finish verified (last verified: ${last_success:0:12})." >&2
    echo "Fix forward with a new commit, or restore service with deploy/scripts/rollback.sh (docs/RUNBOOK-GERI-ALMA.md)." >&2
    exit 1
  fi
  git merge --ff-only "$revision"
fi

log_file="$log_dir/deploy-$(date -u +%Y%m%dT%H%M%SZ)-${revision:0:12}.log"
DEPLOY_REVISION="$revision" bash deploy/scripts/fast-deploy.sh >"$log_file" 2>&1 &
deploy_pid=$!
tail --lines=+1 --follow=name --retry --pid="$deploy_pid" -- "$log_file" &
tail_pid=$!

status=0
wait "$deploy_pid" || status=$?
wait "$tail_pid" || true

if (( status == 0 )); then
  # fast-deploy.sh records .last-success itself; a verified newer release lifts a rollback hold.
  if [[ -n "$hold" ]] && ! is_ancestor_or_equal "$revision" "$hold"; then
    rm -f -- "$hold_file"
    echo "Rollback hold on ${hold:0:12} lifted by the verified deploy of ${revision:0:12}."
  fi
fi

find "$log_dir" -name 'deploy-*.log' -mtime +30 -delete
echo "Deployment of $revision finished with exit code $status; log: $log_file"
exit "$status"
