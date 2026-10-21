#!/usr/bin/env bash
set -Eeuo pipefail

# ExecStop of ravencia-deploy-guard.service: when the host is being stopped (the
# nightly 00:30 stop, or by hand) while a deploy runs, hold the shutdown until
# the deploy releases ~/deploy-logs/.lock (ci-entrypoint.sh keeps it for the
# whole deploy), so it is not cut off in the middle of a backup or migration.
# Bounded: after RAVENCIA_DEPLOY_LOCK_WAIT_SECONDS (default 1380 s = 23 min,
# under the unit's TimeoutStopSec) the shutdown continues anyway.

lock_file="${RAVENCIA_DEPLOY_LOCK:-$HOME/deploy-logs/.lock}"
wait_seconds="${RAVENCIA_DEPLOY_LOCK_WAIT_SECONDS:-1380}"
[[ "$wait_seconds" =~ ^[0-9]+$ ]] || { echo "RAVENCIA_DEPLOY_LOCK_WAIT_SECONDS must be a whole number." >&2; exit 2; }

if [[ ! -e "$lock_file" ]]; then
  echo "No deploy lock file ($lock_file); nothing to wait for."
  exit 0
fi

# Opened read-only: flock does not need write access and the file is not created.
exec 9<"$lock_file"
if flock --nonblock 9; then
  echo "No deploy is running; shutdown continues."
  exit 0
fi

echo "A deploy holds $lock_file; holding the shutdown for up to ${wait_seconds}s."
started="$(date +%s)"
if flock --wait "$wait_seconds" 9; then
  echo "The deploy finished after $(( $(date +%s) - started ))s; shutdown continues."
else
  echo "The deploy still holds the lock after ${wait_seconds}s; shutdown continues anyway." >&2
fi
