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
assert_contains "$sql" "ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO marketplacehub_app" "tables of migrations still run as marketplacehub are granted too"
assert_contains "$sql" "ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO marketplacehub_app" "sequences of migrations still run as marketplacehub are granted too"
assert_contains "$sql" "ALTER ROLE marketplacehub_app SET idle_in_transaction_session_timeout = '5min'" "an idle open transaction of the app is cut after 5 minutes"
assert_eq "1" "$(grep -c '^BEGIN;$' <<<"$sql")" "one transaction"
assert_eq "1" "$(grep -c '^COMMIT;$' <<<"$sql")" "committed once at the end"

status=0
bash "$script" --unknown >/dev/null 2>&1 || status=$?
assert_eq "2" "$status" "an unknown option is refused before anything runs"

# --apply against stubs: sudo runs the command (install without owner/group), docker answers the environment and
# records what psql reads. Before the switch, after it (re-run, runbook section 7 risk b) and with an unusual user.
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/secrets"
cat > "$work/bin/sudo" <<'EOF'
#!/usr/bin/env bash
[[ "${1:-}" == -n ]] && shift
if [[ "$1" == install ]]; then
  shift; args=()
  while (($#)); do case "$1" in -o|-g) shift 2 ;; *) args+=("$1"); shift ;; esac; done
  exec install "${args[@]}"
fi
exec "$@"
EOF
cat > "$work/bin/docker" <<'EOF'
#!/usr/bin/env bash
case "$*" in
  *"printenv POSTGRES_USER"*|*"printenv POSTGRES_DB"*) echo marketplacehub ;;
  *psql*) cat >> "$STUB_PSQL_LOG" ;;
  *) echo "unexpected docker $*" >&2; exit 1 ;;
esac
EOF
chmod +x "$work/bin/sudo" "$work/bin/docker"
apply() { PATH="$work/bin:$PATH" STUB_PSQL_LOG="$work/psql.log" RAVENCIA_SECRETS_ROOT="$work/secrets" bash "$script" --apply 2>&1; }
field() { tr ';' '\n' < "$1" | grep "^$2=" | cut -d= -f2-; }

printf 'Host=postgres;Database=marketplacehub;Username=marketplacehub;Password=superpass1;Maximum Pool Size=25' > "$work/secrets/app_db_connection.txt"
status=0; output="$(apply)" || status=$?
assert_eq "0" "$status" "first --apply runs ($output)"
assert_eq "marketplacehub_owner" "$(field "$work/secrets/migrate_db_connection.txt" Username)" "the migrate file logs in as the owner role"
assert_eq "marketplacehub_app" "$(field "$work/secrets/app_db_connection.app-role.txt" Username)" "the app file logs in as the app role"
assert_eq "25" "$(field "$work/secrets/app_db_connection.app-role.txt" "Maximum Pool Size")" "the app file keeps the connection options"
app_password="$(field "$work/secrets/app_db_connection.app-role.txt" Password)"
owner_password="$(field "$work/secrets/migrate_db_connection.txt" Password)"
assert_contains "$(cat "$work/psql.log")" "\\set app_password '$app_password'" "psql gets the app password on stdin"

# The switch (section 7 step 5), and the app-role file lost: a re-run must keep the password the API logs in with.
cp "$work/secrets/app_db_connection.app-role.txt" "$work/secrets/app_db_connection.txt"
rm -f "$work/secrets/app_db_connection.app-role.txt" "$work/psql.log"
status=0; output="$(apply)" || status=$?
assert_eq "0" "$status" "--apply runs again after the switch ($output)"
assert_eq "$app_password" "$(field "$work/secrets/app_db_connection.app-role.txt" Password)" "the re-run keeps the app password in use"
assert_eq "$owner_password" "$(field "$work/secrets/migrate_db_connection.txt" Password)" "the re-run keeps the owner password"
assert_contains "$(cat "$work/psql.log")" "\\set app_password '$app_password'" "the re-run sets the same app password"

printf 'Host=postgres;Database=marketplacehub;Username=someone_else;Password=x1' > "$work/secrets/app_db_connection.txt"
rm -f "$work/psql.log"
status=0; output="$(apply)" || status=$?
assert_eq "1" "$status" "an unusual user is refused"
assert_eq "" "$(cat "$work/psql.log" 2>/dev/null || true)" "nothing reaches psql for an unusual user"

printf 'Host=postgres;Database=marketplacehub;Username=marketplacehub_app;Password=has-dash' > "$work/secrets/app_db_connection.txt"
status=0; output="$(apply)" || status=$?
assert_eq "1" "$status" "an app password the script cannot parse is not replaced"
assert_eq "" "$(cat "$work/psql.log" 2>/dev/null || true)" "nothing reaches psql then either"

# The restore drill and migration rehearsal restore a dump that grants to both roles: the roles exist there first.
drill="$(cat "$repository_root/deploy/backup/restore-drill.sh")"
roles_at="$(grep -n 'CREATE ROLE marketplacehub_app NOLOGIN' <<<"$drill" | head -n 1 | cut -d: -f1)"
restore_at="$(grep -n 'pg_restore --exit-on-error --no-owner' <<<"$drill" | head -n 1 | cut -d: -f1)"
assert_contains "$drill" "CREATE ROLE marketplacehub_owner NOLOGIN" "the drill database has the owner role"
if [[ -n "$roles_at" && -n "$restore_at" ]] && (( roles_at < restore_at )); then
  pass "the drill creates the roles before pg_restore"
else
  fail "the drill creates the roles before pg_restore (roles line ${roles_at:-none}, pg_restore line ${restore_at:-none})"
fi

# Runbook section 7 step 4 is a prepared, not applied, patch: it must still apply to the tree (regenerate it from the
# runbook's description when a change to those files makes it stale), and the tree must not carry it yet.
patch="$repository_root/docs/patches/VERITABANI-DAGITIM-1-app-db-roles-switch.patch"
status=0
apply_output="$(git -C "$repository_root" apply --check "$patch" 2>&1)" || status=$?
assert_eq "0" "$status" "the section 7 step 4 patch still applies ($apply_output)"
assert_not_contains "$(cat "$repository_root/deploy/compose/compose.yaml")" "migrate_db_connection" "compose does not use the owner connection yet (owner's step)"

callers="$(grep -rl 'install-app-db-roles' "$repository_root/deploy/scripts" "$repository_root/deploy/systemd" "$repository_root/deploy/compose" "$repository_root/deploy/backup" "$repository_root/.github" 2>/dev/null | grep -v 'deploy/scripts/install-app-db-roles.sh' || true)"
assert_eq "" "$callers" "no deploy script, unit, compose file or workflow runs it"

finish "app database roles"
