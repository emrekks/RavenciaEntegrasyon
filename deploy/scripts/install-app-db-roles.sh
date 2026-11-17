#!/usr/bin/env bash
set -Eeuo pipefail

# Least-privilege database roles for the application (audit VERITABANI-DAGITIM-1). Today API, Worker and migrate log
# in as "marketplacehub", the image's POSTGRES_USER, which is a SUPERUSER: one SQL injection could run
# COPY ... TO PROGRAM or DROP DATABASE. This script prepares two ordinary roles:
#   - marketplacehub_owner: LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS; owns the database,
#     the application schemas and every object in them (moved object by object), so migrations run as it;
#   - marketplacehub_app: LOGIN, same flags; CONNECT and TEMP on the database, USAGE on the schemas, SELECT, INSERT,
#     UPDATE, DELETE on every table and USAGE, SELECT, UPDATE on every sequence, also for tables later migrations
#     create (ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub_owner). The code needs nothing else: TRUNCATE only on
#     its own temp tables, advisory locks and LISTEN/NOTIFY are open to every role.
# "marketplacehub" stays the superuser for the backup job, healthwatch and the read-only role script.
#
# NOT run by any deploy. The owner runs it once on the host, then switches the secrets and the migrate service as
# docs/RUNBOOK-GERI-ALMA.md "Uygulama veritabanı rolleri" describes (rollback there too).
#   --print-sql  print the SQL --apply runs (passwords as psql variables), touch nothing
#   --check      show the roles, who owns the objects and which user each session uses (read only; the default)
#   --apply      create or refresh both roles, move ownership, grant, and write
#                deploy/secrets/migrate_db_connection.txt (owner) and deploy/secrets/app_db_connection.app-role.txt
#                (app), root:1654 0640, each from the current app_db_connection.txt with its own user and password.
#                Idempotent and safe to run again after the switch (app_db_connection.txt then names the app role):
#                a password in use or in an existing file is kept. ALTER ... OWNER takes a short ACCESS EXCLUSIVE lock
#                per table; lock_timeout 5s makes a busy table fail the run instead of queueing writes (run it again).
# Also: the app role's idle_in_transaction_session_timeout is 5 min, and default privileges cover tables created by
# marketplacehub_owner and, until migrate switches, by marketplacehub.

mode="${1:---check}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
secrets_root="${RAVENCIA_SECRETS_ROOT:-$repository_root/deploy/secrets}"
container="${RAVENCIA_POSTGRES_CONTAINER:-marketplacehub-postgres-1}"
owner_role=marketplacehub_owner
app_role=marketplacehub_app

apply_sql() {
  cat <<'SQL'
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'marketplacehub_owner') THEN CREATE ROLE marketplacehub_owner LOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'marketplacehub_app') THEN CREATE ROLE marketplacehub_app LOGIN; END IF;
END
$$;
ALTER ROLE marketplacehub_owner WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT PASSWORD :'owner_password';
ALTER ROLE marketplacehub_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT PASSWORD :'app_password';
-- A transaction the code leaves open and idle (a bug, a hung call) no longer holds its locks and blocks vacuum for good.
ALTER ROLE marketplacehub_app SET idle_in_transaction_session_timeout = '5min';
-- Ownership moves object by object: marketplacehub is the bootstrap superuser (it owns the system catalogs), so
-- REASSIGN OWNED is refused for that role. Extension objects (pg_trgm) stay with it.
ALTER DATABASE marketplacehub OWNER TO marketplacehub_owner;
DO $$
DECLARE item record;
BEGIN
  FOR item IN SELECT n.nspname FROM pg_namespace n
              WHERE n.nspowner = 'marketplacehub'::regrole AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema' LOOP
    EXECUTE format('ALTER SCHEMA %I OWNER TO marketplacehub_owner', item.nspname);
  END LOOP;
  -- Tables, views and materialized views first: ALTER TABLE ... OWNER also moves the indexes and owned sequences.
  FOR item IN SELECT c.oid::regclass AS name, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE c.relowner = 'marketplacehub'::regrole AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
                AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e') LOOP
    EXECUTE format('ALTER %s %s OWNER TO marketplacehub_owner',
      CASE item.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'f' THEN 'FOREIGN TABLE' ELSE 'TABLE' END, item.name);
  END LOOP;
  FOR item IN SELECT c.oid::regclass AS name FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE c.relowner = 'marketplacehub'::regrole AND c.relkind = 'S'
                AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
                AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('e', 'i', 'a')) LOOP
    EXECUTE format('ALTER SEQUENCE %s OWNER TO marketplacehub_owner', item.name);
  END LOOP;
  -- The application's own functions (trigger functions in integration and catalog), not pg_trgm's.
  FOR item IN SELECT p.oid::regprocedure AS name FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
              WHERE p.proowner = 'marketplacehub'::regrole AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
                AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e') LOOP
    EXECUTE format('ALTER ROUTINE %s OWNER TO marketplacehub_owner', item.name);
  END LOOP;
  FOR item IN SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO marketplacehub_app', item.nspname);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO marketplacehub_app', item.nspname);
    EXECUTE format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO marketplacehub_app', item.nspname);
  END LOOP;
END
$$;
GRANT CONNECT, TEMPORARY ON DATABASE marketplacehub TO marketplacehub_app;
-- What later migrations (run as marketplacehub_owner) create, new schemas included.
ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub_owner GRANT USAGE ON SCHEMAS TO marketplacehub_app;
ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub_owner GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO marketplacehub_app;
ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub_owner GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO marketplacehub_app;
-- Until the migrate service switches to marketplacehub_owner (runbook section 7 step 4), migrations still run as
-- marketplacehub: their new tables are reachable for the app role too (no 42501 after the switch). Their ownership
-- moves to marketplacehub_owner on the next --apply.
ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub GRANT USAGE ON SCHEMAS TO marketplacehub_app;
ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO marketplacehub_app;
ALTER DEFAULT PRIVILEGES FOR ROLE marketplacehub GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO marketplacehub_app;
COMMIT;
SQL
}

check_sql() {
  cat <<'SQL'
\set ON_ERROR_STOP on
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin
FROM pg_roles WHERE rolname IN ('marketplacehub', 'marketplacehub_owner', 'marketplacehub_app', 'ravencia_ro') ORDER BY 1;
SELECT pg_get_userbyid(c.relowner) AS owner, count(*) AS objects
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema' GROUP BY 1 ORDER BY 1;
SELECT pg_get_userbyid(datdba) AS database_owner FROM pg_database WHERE datname = current_database();
SELECT usename, count(*) AS sessions FROM pg_stat_activity WHERE datname = current_database() GROUP BY 1 ORDER BY 1;
SQL
}

case "$mode" in
  --print-sql) apply_sql; exit 0 ;;
  --check|--apply) ;;
  *) echo "Usage: $0 [--check | --print-sql | --apply]" >&2; exit 2 ;;
esac

if (( EUID == 0 )); then sudo_cmd=(); else sudo_cmd=(sudo -n); fi
admin_user="$("${sudo_cmd[@]}" docker exec "$container" printenv POSTGRES_USER)"
database="$("${sudo_cmd[@]}" docker exec "$container" printenv POSTGRES_DB)"
[[ "$admin_user" == marketplacehub && "$database" == marketplacehub ]] \
  || { echo "Expected POSTGRES_USER and POSTGRES_DB marketplacehub, found $admin_user / $database." >&2; exit 1; }
psql_cmd=("${sudo_cmd[@]}" docker exec -i "$container" psql -X -q -U "$admin_user" -d "$database")

if [[ "$mode" == --check ]]; then
  check_sql | "${psql_cmd[@]}"
  exit 0
fi

current="$secrets_root/app_db_connection.txt"
[[ -s "$current" ]] || { echo "Missing $current." >&2; exit 1; }
connection="$("${sudo_cmd[@]}" cat -- "$current")"
# Before the switch the file names the superuser; after it (runbook section 7 step 5) the app role. Both are fine: the
# run only takes host, database and options from it. Re-running after the switch is how objects created by hand or by
# a migration that still ran as marketplacehub are moved to the owner role (runbook section 7, risk b; section 3).
connection_user=""
if [[ "$connection" =~ (^|;)Username=([A-Za-z0-9_]+)(;|$) ]]; then connection_user="${BASH_REMATCH[2]}"; fi
case "$connection_user" in
  marketplacehub|"$app_role") ;;
  *) echo "app_db_connection.txt uses Username=${connection_user:-<none>}, expected marketplacehub or $app_role; the file is unusual." >&2; exit 1 ;;
esac

# Passwords are never changed under a running connection: when app_db_connection.txt already logs in as the role, its
# password is kept; else the password of the role's existing file; else a new random one.
password_of() {
  if [[ "$1" =~ (^|;)Password=([A-Za-z0-9]+)(;|$) ]]; then printf '%s' "${BASH_REMATCH[2]}"; else return 1; fi
}
password_from() {
  local file="$1" role="$2" existing
  if [[ "$connection_user" == "$role" ]]; then
    password_of "$connection" && return 0
    echo "app_db_connection.txt logs in as $role with a password this script does not parse; not changing it." >&2
    return 1
  fi
  if existing="$("${sudo_cmd[@]}" cat -- "$file" 2>/dev/null)" && password_of "$existing"; then
    return 0
  fi
  openssl rand -hex 24
}
owner_file="$secrets_root/migrate_db_connection.txt"
app_file="$secrets_root/app_db_connection.app-role.txt"
owner_password="$(password_from "$owner_file" "$owner_role")"
app_password="$(password_from "$app_file" "$app_role")"
[[ -n "$owner_password" && -n "$app_password" ]] || { echo "Could not determine the role passwords." >&2; exit 1; }

with_user() { # the current connection string with Username and Password replaced
  local part parts=() out=()
  IFS=';' read -r -a parts <<<"$connection"
  for part in "${parts[@]}"; do
    case "$part" in
      Username=*) out+=("Username=$1") ;;
      Password=*) out+=("Password=$2") ;;
      *) out+=("$part") ;;
    esac
  done
  (IFS=';'; printf '%s' "${out[*]}")
}
write_secret() {
  local file="$1" content="$2" temporary
  temporary="$(mktemp)"
  printf '%s' "$content" > "$temporary"
  "${sudo_cmd[@]}" install -o root -g 1654 -m 0640 -- "$temporary" "$file"
  rm -f -- "$temporary"
}

# The passwords go in on stdin (psql variables), never on a command line other users could list.
{ printf "\\\\set owner_password '%s'\n\\\\set app_password '%s'\n" "$owner_password" "$app_password"; apply_sql; } | "${psql_cmd[@]}"
write_secret "$owner_file" "$(with_user "$owner_role" "$owner_password")"
write_secret "$app_file" "$(with_user "$app_role" "$app_password")"
echo "Roles $owner_role and $app_role are ready; wrote ${owner_file#"$repository_root"/} and ${app_file#"$repository_root"/}."
if [[ "$connection_user" == "$app_role" ]]; then
  echo "API and Worker already log in as $app_role; ownership and grants are refreshed, passwords unchanged."
else
  echo "Nothing uses them yet: follow docs/RUNBOOK-GERI-ALMA.md \"Uygulama veritabanı rolleri\" to switch."
fi
check_sql | "${psql_cmd[@]}"
