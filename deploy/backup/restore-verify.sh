#!/bin/sh
set -eu
test "$#" -eq 1 || { echo "usage: restore-verify.sh /backup/TIMESTAMP" >&2; exit 2; }
set_dir="$1"
cd "$set_dir"
sha256sum -c SHA256SUMS
test -s database.dump
test -s private-volumes.tar.gz
pg_restore --list database.dump >/dev/null
echo "Backup artifacts passed integrity and archive checks; full restore requires an empty isolated target database."
