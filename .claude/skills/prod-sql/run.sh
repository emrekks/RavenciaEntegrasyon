#!/usr/bin/env bash
# Read-only SQL on the live database. The server's ravencia-ro command wraps the statements in
# BEGIN READ ONLY ... ROLLBACK on a session opened with default_transaction_read_only=on, and refuses
# anything but SELECT / WITH / VALUES / TABLE / EXPLAIN / SHOW, so nothing can be written even by mistake.
# Usage: run.sh --columns schema.table | run.sh -c "SQL" | run.sh file.sql
set -u
host="${RAVENCIA_SSH_HOST:-ravencia-ro@187.126.24.163}"
# Default: the read-only ravencia-ro login (key without passphrase, forced command on the server).
# RAVENCIA_SSH_KEY picks another key; another RAVENCIA_SSH_HOST without a key uses the Windows ssh-agent,
# which only Windows OpenSSH can reach (the request then runs through the same ravencia-ro command).
if [ -n "${RAVENCIA_SSH_KEY:-}" ] || [[ "$host" == ravencia-ro@* ]]; then
  sshx=(ssh -i "${RAVENCIA_SSH_KEY:-$HOME/.ssh/ravencia_ro}" -o IdentitiesOnly=yes -o BatchMode=yes)
elif [ -x /c/Windows/System32/OpenSSH/ssh.exe ]; then sshx=(/c/Windows/System32/OpenSSH/ssh.exe -o BatchMode=yes)
else sshx=(ssh -o BatchMode=yes); fi

case "${1:-}" in
  --columns)
    table="${2:?schema.table}"
    schema="${table%%.*}"; name="${table#*.}"
    sql="SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = '${schema//\'/}' AND table_name = '${name//\'/}' ORDER BY ordinal_position;"
    ;;
  -c) sql="${2:?SQL}" ;;
  "") echo "usage: run.sh --columns schema.table | -c \"SQL\" | file.sql" >&2; exit 2 ;;
  *) sql="$(cat "$1")" || exit 2 ;;
esac

printf '%s\n' "$sql" | "${sshx[@]}" "$host" 'ravencia-ro sql'
