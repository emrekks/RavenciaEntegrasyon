#!/usr/bin/env bash
set -Eeuo pipefail

# Forced command for the GitHub Actions deploy key. It is installed root-owned at
# /usr/local/bin/ravencia-ci-deploy by install-ci-deploy.sh, so a repository pull
# can never change what the key is allowed to run. The only accepted input is the
# 40-character commit SHA that passed the Validate workflow.

# A dropped SSH session must not interrupt a deployment in the middle of a migration.
trap '' HUP

revision="${SSH_ORIGINAL_COMMAND:-}"
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo "Usage: ssh <host> <40-character commit SHA>" >&2; exit 2; }

repository_root="${RAVENCIA_REPOSITORY_ROOT:-$HOME/RavenciaEntegrasyon}"
log_dir="$HOME/deploy-logs"
mkdir -p -- "$log_dir"

# The deployment child inherits this descriptor, so the lock outlives the SSH session.
exec 9>"$log_dir/.lock"
flock -w 3600 9 || { echo "Another deployment is still running." >&2; exit 75; }

cd "$repository_root"
git fetch --quiet origin main
if [[ "$(git rev-parse HEAD)" != "$revision" ]]; then
  if git merge-base --is-ancestor "$revision" HEAD; then
    echo "Revision $revision is already contained in the checked-out history; a newer deployment owns it."
    exit 0
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

find "$log_dir" -name 'deploy-*.log' -mtime +30 -delete
echo "Deployment of $revision finished with exit code $status; log: $log_file"
exit "$status"
