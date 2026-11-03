#!/usr/bin/env bash
# Read-only look at the live containers: which image each one runs, their state, and their logs.
# Requests go to the server's ravencia-ro command (fixed docker ps / inspect / logs and journalctl reads);
# nothing on the server is changed.
# Usage: run.sh status | run.sh logs <api|worker|caddy|postgres> [--since 30m] [--tail N] [--grep REGEX]
#        run.sh host [--since 6h] | run.sh errors [--since 30m] | run.sh journal <api|worker|caddy|postgres> [--since 30m] [--grep REGEX]
set -u
host="${RAVENCIA_SSH_HOST:-ravencia-ro@187.126.24.163}"
# Default: the read-only ravencia-ro login (key without passphrase, forced command on the server).
# RAVENCIA_SSH_KEY picks another key; another RAVENCIA_SSH_HOST without a key uses the Windows ssh-agent,
# which only Windows OpenSSH can reach (the request then runs through the same ravencia-ro command).
if [ -n "${RAVENCIA_SSH_KEY:-}" ] || [[ "$host" == ravencia-ro@* ]]; then
  sshx=(ssh -i "${RAVENCIA_SSH_KEY:-$HOME/.ssh/ravencia_ro}" -o IdentitiesOnly=yes -o BatchMode=yes)
elif [ -x /c/Windows/System32/OpenSSH/ssh.exe ]; then sshx=(/c/Windows/System32/OpenSSH/ssh.exe -o BatchMode=yes)
else sshx=(ssh -o BatchMode=yes); fi

remote() { "${sshx[@]}" "$host" "ravencia-ro $*"; }

usage() { echo "usage: run.sh status | host [--since 6h] | logs <api|worker|caddy|postgres> [--since 30m] [--tail N] [--grep REGEX] | errors [--since 30m] | journal <service> [--since 30m] [--grep REGEX]" >&2; exit 2; }

since="30m"; tail_n="-"; pattern=""
parse_opts() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --since) since="${2:?--since 30m | 2h | 2026-10-31T09:00:00}"; shift 2 ;;
      --tail) tail_n="${2:?--tail N}"; shift 2 ;;
      --grep) pattern="${2:?--grep REGEX}"; shift 2 ;;
      *) usage ;;
    esac
  done
  [[ "$since" =~ ^[0-9]+[smhd]$ || "$since" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?)?Z?$ ]] || { echo "bad --since: $since" >&2; exit 2; }
  [ "$tail_n" = "-" ] || [[ "$tail_n" =~ ^[0-9]+$ ]] || { echo "bad --tail: $tail_n" >&2; exit 2; }
}
# The pattern travels base64-encoded, so the server never parses it as shell words.
encoded_pattern() { [ -z "$pattern" ] || printf '%s' "$pattern" | base64 -w0; }

case "${1:-}" in
  status) remote status ;;
  host)
    shift; since="6h"; parse_opts "$@"
    remote host "$since"
    ;;
  logs)
    svc="${2:-}"; shift 2 2>/dev/null || usage
    case "$svc" in api|worker|caddy|postgres) ;; *) usage ;; esac
    parse_opts "$@"
    remote logs "$svc" "$since" "$tail_n" "$(encoded_pattern)"
    ;;
  errors)
    shift; parse_opts "$@"
    remote errors "$since"
    ;;
  journal)
    svc="${2:-}"; shift 2 2>/dev/null || usage
    case "$svc" in api|worker|caddy|postgres) ;; *) usage ;; esac
    parse_opts "$@"
    remote journal "$svc" "$since" "$(encoded_pattern)"
    ;;
  *) usage ;;
esac
