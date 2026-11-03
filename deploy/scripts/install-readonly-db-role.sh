#!/usr/bin/env bash
set -Eeuo pipefail

# Creates or refreshes the PostgreSQL role "ravencia_ro" used by the read-only login's `sql`
# request (readonly-entrypoint.sh). The role, not the entrypoint's statement filter, is the boundary:
#   - LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS, at most 3 connections;
#   - member of pg_read_all_data only (SELECT on every table, nothing else: no writes, no
#     COPY ... TO PROGRAM, no server files, no pg_terminate_backend on other roles);
#   - default_transaction_read_only=on and statement_timeout=60s as role settings;
#   - no password: it can only log in over the container's unix socket (pg_hba "local ... trust"),
#     i.e. through `docker exec`, which needs root. Network logins require a password (scram).
# Idempotent; run as ubuntu (sudo) or root. Writes only role metadata, no table data.

if (( EUID == 0 )); then docker_cmd=(docker); else docker_cmd=(sudo -n docker); fi
container="${RAVENCIA_POSTGRES_CONTAINER:-marketplacehub-postgres-1}"
admin_user="$("${docker_cmd[@]}" exec "$container" printenv POSTGRES_USER)"
database="$("${docker_cmd[@]}" exec "$container" printenv POSTGRES_DB)"

"${docker_cmd[@]}" exec -i "$container" psql -X -v ON_ERROR_STOP=1 -U "$admin_user" -d "$database" <<'SQL'
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ravencia_ro') THEN
    CREATE ROLE ravencia_ro LOGIN;
  END IF;
END
$$;
ALTER ROLE ravencia_ro WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT
  CONNECTION LIMIT 3 PASSWORD NULL;
ALTER ROLE ravencia_ro SET default_transaction_read_only = on;
ALTER ROLE ravencia_ro SET statement_timeout = '60s';
ALTER ROLE ravencia_ro SET idle_in_transaction_session_timeout = '60s';
GRANT pg_read_all_data TO ravencia_ro;
SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolconnlimit,
       pg_has_role('ravencia_ro', 'pg_read_all_data', 'MEMBER') AS reads_all,
       pg_has_role('ravencia_ro', 'pg_write_all_data', 'MEMBER') AS writes_all,
       pg_has_role('ravencia_ro', 'pg_execute_server_program', 'MEMBER') AS server_program
FROM pg_roles WHERE rolname = 'ravencia_ro';
SQL
