#!/usr/bin/env bash
# deploy/scripts/install-app-db-roles.sh (VERITABANI-DAGITIM-1): the SQL it would run grants no superuser power, and
# nothing in the automatic deploy calls it (the owner runs it by hand, docs/RUNBOOK-GERI-ALMA.md).
set -Eeuo pipefail

tests_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$tests_dir/../.." && pwd -P)"
# shellcheck source=tests/ops/assert.sh
source "$tests_dir/assert.sh"

script="$repository_root/deploy/scripts/install-app-db-roles.sh"
echo "app database roles"

sql="$(bash "$script" --print-sql)"
assert_contains "$sql" "ALTER ROLE marketplacehub_owner WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" "the owner role is an ordinary role"
assert_contains "$sql" "ALTER ROLE marketplacehub_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" "the app role is an ordinary role"
assert_contains "$sql" "SET LOCAL lock_timeout = '5s'" "ownership changes give up on a busy table instead of queueing writes"
assert_contains "$sql" "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO marketplacehub_app" "the app role gets row access only"
assert_contains "$sql" "ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub_owner GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO marketplacehub_app" "tables of later migrations are granted too"
assert_contains "$sql" "PASSWORD :'app_password'" "passwords come in as psql variables, not in the script"
assert_not_contains "$sql" "REASSIGN OWNED BY marketplacehub " "no REASSIGN OWNED of the bootstrap superuser (PostgreSQL refuses it)"
for forbidden in "TO marketplacehub_app WITH" "pg_execute_server_program" "pg_write_server_files" "TRUNCATE" "CREATEROLE INHERIT" "GRANT ALL"; do
  assert_not_contains "$sql" "$forbidden" "grants nothing like '$forbidden'"
done
assert_eq "1" "$(grep -c '^BEGIN;$' <<<"$sql")" "one transaction"
assert_eq "1" "$(grep -c '^COMMIT;$' <<<"$sql")" "committed once at the end"

status=0
bash "$script" --unknown >/dev/null 2>&1 || status=$?
assert_eq "2" "$status" "an unknown option is refused before anything runs"

callers="$(cd -- "$repository_root" && grep -rl 'install-app-db-roles' deploy/scripts deploy/systemd deploy/compose deploy/backup .github 2>/dev/null | grep -v 'deploy/scripts/install-app-db-roles.sh' || true)"
assert_eq "" "$callers" "no deploy script, unit, compose file or workflow runs it"

finish "app database roles"
