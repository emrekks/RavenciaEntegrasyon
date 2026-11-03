#!/usr/bin/env bash
# Read-only look at the live containers: which image each one runs, their state, and their logs.
# Only fixed docker ps / inspect / logs commands are sent; nothing on the server is changed.
# Usage: run.sh status | run.sh logs <api|worker|caddy|postgres> [--since 30m] [--tail N] [--grep REGEX]
#        run.sh errors [--since 30m]
set -u
host="${RAVENCIA_SSH_HOST:-ubuntu@187.126.24.163}"
# The host key has a passphrase and lives in the Windows ssh-agent, which only Windows OpenSSH can reach.
if [ -n "${RAVENCIA_SSH_KEY:-}" ]; then sshx=(ssh -i "$RAVENCIA_SSH_KEY" -o BatchMode=yes)
elif [ -x /c/Windows/System32/OpenSSH/ssh.exe ]; then sshx=(/c/Windows/System32/OpenSSH/ssh.exe -o BatchMode=yes)
else sshx=(ssh -o BatchMode=yes); fi

remote() { "${sshx[@]}" "$host" "$1"; }

usage() { echo "usage: run.sh status | logs <api|worker|caddy|postgres> [--since 30m] [--tail N] [--grep REGEX] | errors [--since 30m]" >&2; exit 2; }

since="30m"; tail_n=""; pattern=""
parse_opts() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --since) since="${2:?--since 30m | 2h | 2026-10-31T09:00:00}"; shift 2 ;;
      --tail) tail_n="${2:?--tail N}"; shift 2 ;;
      --grep) pattern="${2:?--grep REGEX}"; shift 2 ;;
      *) usage ;;
    esac
  done
  [[ "$since" =~ ^[0-9A-Za-z:.+-]+$ ]] || { echo "bad --since: $since" >&2; exit 2; }
  [ -z "$tail_n" ] || [[ "$tail_n" =~ ^[0-9]+$ ]] || { echo "bad --tail: $tail_n" >&2; exit 2; }
}

case "${1:-}" in
  status)
    remote 'sudo docker ps -a --filter name=marketplacehub- --format "{{.Names}}" | sort | while read -r c; do
      sudo docker inspect -f "{{.Name}}  {{.Config.Image}}  {{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}  started {{.State.StartedAt}}  restarts {{.RestartCount}}" "$c"
    done | sed "s#^/##"'
    ;;
  logs)
    svc="${2:-}"; shift 2 2>/dev/null || usage
    case "$svc" in api|worker|caddy|postgres) ;; *) usage ;; esac
    parse_opts "$@"
    cmd="sudo docker logs --timestamps --since $since"
    [ -n "$tail_n" ] && cmd="$cmd --tail $tail_n"
    cmd="$cmd marketplacehub-$svc-1 2>&1"
    [ -n "$pattern" ] && cmd="$cmd | grep -E -i -- $(printf '%q' "$pattern")"
    remote "$cmd | tail -n 400"
    ;;
  errors)
    shift; parse_opts "$@"
    remote "for c in api worker; do echo \"== \$c\"; sudo docker logs --timestamps --since $since marketplacehub-\$c-1 2>&1 \
      | grep -E '\"@l\":\"(Error|Fatal)\"' | cut -c1-500 | tail -n 80; done"
    ;;
  *) usage ;;
esac
