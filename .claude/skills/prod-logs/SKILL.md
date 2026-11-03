---
name: prod-logs
description: Reads the live containers on EC2 (marketplacehub-api-1, -worker-1, -caddy-1, -postgres-1) without changing anything - which image and sha each one runs, its state and health, and its recent logs filtered by time or a pattern. Use to check a deploy reached live, to see why a job or request failed, or to look for errors after a change.
argument-hint: "[status | logs <api|worker|caddy|postgres> [--since 30m] [--tail N] [--grep REGEX] | errors [--since 30m] | journal <service> [--since 30m] [--grep REGEX]]"
---

# Read-only logs and state on live

The live server is read-only for us: never restart, edit or write anything there. The script logs in as the read-only
`ravencia-ro` user (key `~/.ssh/ravencia_ro`), whose server-side command only runs fixed
`docker ps`, `docker inspect`, `docker logs` and `journalctl` reads. For data, use `/prod-sql` instead.

```bash
# Image (sha tag), state, health, start time and restart count of every container:
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-logs/run.sh" status

# One container's logs; --since takes 30m, 2h or an ISO time, --grep an extended regex (case-insensitive):
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-logs/run.sh" logs worker --since 2h --grep 'Trendyol|cursor'
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-logs/run.sh" logs api --tail 200

# Error and Fatal lines from api and worker together, each cut to 500 characters:
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-logs/run.sh" errors --since 1h

# The journald copy (kept across deploys, 30 days); Caddy access log lines are JSON:
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-logs/run.sh" journal caddy --since 2h --grep "\"status\":5"
```

- Logs are Serilog compact JSON: `@t` time, `@m` message, `@l` level (left out for Information), `@x` exception.
  `--grep '"@l":"Warning"'` finds warnings; `--grep 'TRENDYOL_ORDER_SYNC'` follows one job type.
- Output is capped at the last 400 lines (80 per container for `errors`); narrow with `--since` or `--grep`
  instead of raising it.
- A deploy is live only when `status` shows the shipped sha on api, worker and caddy.
- Log lines can hold customer data (names, addresses, phones): quote ids, states and error messages to the user,
  not personal fields.
