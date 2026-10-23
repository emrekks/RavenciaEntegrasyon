#!/usr/bin/env bash
# Read-only SQL on the live database. The statements run inside BEGIN READ ONLY ... ROLLBACK on a session opened with
# default_transaction_read_only=on, so nothing can be written even by mistake.
# Usage: run.sh --columns schema.table | run.sh -c "SQL" | run.sh file.sql
set -u
key="${RAVENCIA_SSH_KEY:-C:/Users/emrek/Desktop/İŞ/Ravencia.pem}"
host="${RAVENCIA_SSH_HOST:-ubuntu@63.180.140.51}"

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

printf 'BEGIN READ ONLY;\n%s\n;\nROLLBACK;\n' "$sql" | ssh -i "$key" -o BatchMode=yes "$host" '
  user=$(sudo docker exec marketplacehub-postgres-1 printenv POSTGRES_USER)
  db=$(sudo docker exec marketplacehub-postgres-1 printenv POSTGRES_DB)
  sudo docker exec -i -e PGOPTIONS="-c default_transaction_read_only=on" marketplacehub-postgres-1 \
    psql -U "$user" -d "$db" -v ON_ERROR_STOP=1 -P pager=off'
