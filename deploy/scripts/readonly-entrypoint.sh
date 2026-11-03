#!/usr/bin/env bash
set -Eeuo pipefail

# Forced command of the read-only "ravencia-ro" login (Claude's /prod-sql, /prod-logs and the
# ship image check). Installed root-owned at /usr/local/bin/ravencia-ro by
# install-readonly-access.sh; the login's only sudo right is running this script as root.
# Nothing here changes the server: the accepted requests are
#   sql                                        SQL on stdin, run as the read-only database role ravencia_ro
#                                              inside BEGIN READ ONLY ... ROLLBACK (statement allowlist below)
#   status                                     image, state, health, start and restarts of every container
#   images                                     image of caddy, api and worker (ship.sh)
#   host [<since>]                             disk, ravencia-* timers and journal, failed units, backup
#                                              sets with sizes, CrowdSec simulation/alerts/decisions/metrics
#   logs <service> <since> [<tail>|-] [<b64>]  docker logs, optionally filtered by a base64 extended regex
#   errors <since>                             Error/Fatal lines of api and worker
#   journal <service> <since> [<b64>]          journalctl CONTAINER_NAME=marketplacehub-<service>-1 -o cat
# <service> is api, worker, caddy or postgres; <since> is like 30m, 2h or 2026-11-03T09:00:00.
# A leading "ravencia-ro" word is ignored, so the same request also works through a full shell
# (ssh ubuntu@host 'ravencia-ro status').

request="${SSH_ORIGINAL_COMMAND:-${1:-}}"
self=/usr/local/bin/ravencia-ro
if (( EUID != 0 )); then
  exec sudo -n "$self" "$request"
fi

read -r -a words <<<"$request"
[[ "${words[0]:-}" != ravencia-ro ]] || words=("${words[@]:1}")
verb="${words[0]:-}"

fail() { echo "ravencia-ro: $*" >&2; exit 2; }
service_arg() {
  case "${1:-}" in api|worker|caddy|postgres) printf '%s' "$1" ;; *) fail "service must be api, worker, caddy or postgres" ;; esac
}
since_arg() {
  [[ "${1:-}" =~ ^[0-9]+[smhd]$ || "${1:-}" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}([T\ ][0-9]{2}:[0-9]{2}(:[0-9]{2})?)?Z?$ ]] || fail "bad since: ${1:-}"
  # docker logs takes Go durations, which have no days.
  if [[ "$1" =~ ^([0-9]+)d$ ]]; then printf '%sh' "$(( BASH_REMATCH[1] * 24 ))"; else printf '%s' "$1"; fi
}
pattern_arg() {
  local decoded
  [[ -z "${1:-}" ]] && return 0
  [[ "$1" =~ ^[A-Za-z0-9+/=]+$ ]] || fail "pattern must be base64"
  decoded="$(printf '%s' "$1" | base64 -d 2>/dev/null)" || fail "pattern must be base64"
  printf '%s' "$decoded"
}
filter() {
  if [[ -n "$1" ]]; then grep -E -i -- "$1" || true; else cat; fi
}

case "$verb" in
  sql)
    (( ${#words[@]} == 1 )) || fail "usage: sql (statements on stdin)"
    sql="$(head -c 1048576)"
    # The boundary is the database role: psql connects as "ravencia_ro" (install-readonly-db-role.sh),
    # a non-superuser that only holds pg_read_all_data, with default_transaction_read_only=on and a
    # 60 s statement_timeout. Even a statement that slipped past the checks below could not write,
    # run COPY ... TO PROGRAM or read server files. The checks are a second layer that keeps
    # mistakes from reaching the server:
    #  - psql meta-commands other than \echo and \x (\! runs a shell in the container, \o and \copy write files);
    #  - comments other than whole "--" lines, and block comments: a "--" inside a string literal
    #    could otherwise hide a statement start from the check below;
    #  - statements that do not start with SELECT, WITH, VALUES, TABLE, EXPLAIN or SHOW (a ";" inside
    #    a string literal splits there too, which only ever refuses more);
    #  - server functions with side effects.
    # Backslash escapes inside string literals are refused too; write character classes instead.
    while IFS= read -r token; do
      [[ "$token" == '\echo' || "$token" == '\x' ]] || fail "psql meta-command $token is not allowed"
    done < <(printf '%s\n' "$sql" | grep -oE '\\[A-Za-z!?]+' || true)
    [[ "$sql" != *'/*'* ]] || fail "block comments are not allowed"
    body="$(printf '%s\n' "$sql" | grep -vE '^[[:space:]]*--' | grep -vE "^[[:space:]]*[\\\\]" || true)"
    [[ "$body" != *'--'* ]] || fail "comments are allowed only on lines of their own (and no \"--\" inside strings)"
    if printf '%s' "$body" | grep -qiE '(pg_terminate_backend|pg_cancel_backend|pg_reload_conf|pg_rotate_logfile|pg_promote|pg_switch_wal|pg_create_restore_point|pg_logical_emit_message|pg_file_write|pg_advisory|set_config|lo_[a-z]+|dblink)[[:space:]]*\('; then
      fail "server functions with side effects are not allowed"
    fi
    while IFS= read -r statement; do
      statement="${statement#"${statement%%[![:space:]]*}"}"
      [[ -z "$statement" ]] && continue
      first="${statement%%[^A-Za-z]*}"
      case "${first,,}" in
        select|with|values|table|explain|show) ;;
        *) fail "only SELECT, WITH, VALUES, TABLE, EXPLAIN and SHOW statements are allowed (got: ${first:-?})" ;;
      esac
    done < <(printf '%s' "$body" | tr '\n' ' ' | tr ';' '\n')
    db="$(docker exec marketplacehub-postgres-1 printenv POSTGRES_DB)"
    printf 'BEGIN READ ONLY;\n%s\n;\nROLLBACK;\n' "$sql" \
      | docker exec -i -e PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=60000" \
          marketplacehub-postgres-1 psql -U ravencia_ro -d "$db" -X -v ON_ERROR_STOP=1 -P pager=off
    ;;
  status)
    (( ${#words[@]} == 1 )) || fail "usage: status"
    docker ps -a --filter name=marketplacehub- --format '{{.Names}}' | sort | while read -r container; do
      docker inspect -f '{{.Name}}  {{.Config.Image}}  {{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}  started {{.State.StartedAt}}  restarts {{.RestartCount}}' "$container"
    done | sed 's#^/##'
    ;;
  images)
    (( ${#words[@]} == 1 )) || fail "usage: images"
    docker inspect -f '{{.Config.Image}}' marketplacehub-caddy-1 marketplacehub-api-1 marketplacehub-worker-1
    ;;
  logs)
    (( ${#words[@]} >= 3 && ${#words[@]} <= 5 )) || fail "usage: logs <service> <since> [<tail>|-] [<base64 regex>]"
    service="$(service_arg "${words[1]}")"
    since="$(since_arg "${words[2]}")"
    tail_n="${words[3]:--}"
    [[ "$tail_n" == - || "$tail_n" =~ ^[0-9]{1,6}$ ]] || fail "bad tail: $tail_n"
    pattern="$(pattern_arg "${words[4]:-}")"
    args=(--timestamps --since "$since")
    [[ "$tail_n" == - ]] || args+=(--tail "$tail_n")
    docker logs "${args[@]}" "marketplacehub-$service-1" 2>&1 | filter "$pattern" | tail -n 400
    ;;
  errors)
    (( ${#words[@]} == 2 )) || fail "usage: errors <since>"
    since="$(since_arg "${words[1]}")"
    for service in api worker; do
      echo "== $service"
      docker logs --timestamps --since "$since" "marketplacehub-$service-1" 2>&1 \
        | grep -E '"@l":"(Error|Fatal)"' | cut -c1-500 | tail -n 80 || true
    done
    ;;
  journal)
    (( ${#words[@]} == 3 || ${#words[@]} == 4 )) || fail "usage: journal <service> <since> [<base64 regex>]"
    service="$(service_arg "${words[1]}")"
    since="$(since_arg "${words[2]}")"
    pattern="$(pattern_arg "${words[3]:-}")"
    [[ "$since" =~ ^[0-9]+[smhd]$ ]] && since="-$since"
    journalctl --no-pager -o cat --since "${since/T/ }" "CONTAINER_NAME=marketplacehub-$service-1" | filter "$pattern" | tail -n 400
    ;;
  host)
    (( ${#words[@]} == 1 || ${#words[@]} == 2 )) || fail "usage: host [<since>]"
    since="$(since_arg "${words[1]:-6h}")"
    [[ "$since" =~ ^[0-9]+[smh]$ ]] && since="-$since"
    echo "== disk"
    df -h / /var/lib/docker 2>/dev/null | awk '!seen[$0]++'
    echo "== ravencia timers"
    systemctl list-timers --all --no-pager 'ravencia-*' | head -n 20
    echo "== failed units"
    systemctl --failed --no-pager --no-legend || true
    echo "== ravencia unit journal since ${since/T/ }"
    journalctl --no-pager -o short-iso --since "${since/T/ }" -u 'ravencia-*' | grep -vE ' (sudo|sudo-rs)\[| pam_unix\(' | tail -n 150 || true
    echo "== backup sets"
    mountpoint="$(docker volume inspect --format '{{.Mountpoint}}' marketplacehub_backup_staging 2>/dev/null || true)"
    if [[ -n "$mountpoint" ]]; then
      find "$mountpoint" -mindepth 1 -maxdepth 1 -type d -name '????????T??????Z' -print0 | sort -z \
        | xargs -0 -r du -sh | sed "s#$mountpoint/##"
    fi
    if command -v cscli >/dev/null; then
      echo "== crowdsec"
      cscli simulation status 2>/dev/null || true
      cscli alerts list --limit 20 2>/dev/null || true
      cscli decisions list 2>/dev/null | head -n 30 || true
      cscli metrics show acquisition 2>/dev/null || true
    fi
    ;;
  *)
    fail "allowed requests: sql | status | images | host [<since>] | logs <service> <since> [<tail>|-] [<base64 regex>] | errors <since> | journal <service> <since> [<base64 regex>]"
    ;;
esac
