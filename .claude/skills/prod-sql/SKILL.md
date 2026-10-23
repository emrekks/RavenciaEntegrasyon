---
name: prod-sql
description: Runs read-only SQL against the live PostgreSQL (EC2, docker marketplacehub-postgres-1) - wrapped in BEGIN READ ONLY ... ROLLBACK on a read-only session - and lists a table's real column names before a query is written. Use to check live data, sync cursors, jobs or report numbers.
argument-hint: "[--columns schema.table | -c \"SQL\" | file.sql]"
---

# Read-only SQL on live

The live server is read-only for us: never change anything there. This script only reads.

```bash
# The real column names of a table first: EF names them in PascalCase and they must be quoted ("OrderNumber").
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-sql/run.sh" --columns sales.return_claims

# One query, or several in a file (\echo lines label the results):
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-sql/run.sh" -c 'SELECT "Status", count(*) FROM integration.jobs GROUP BY 1'
bash "$CLAUDE_PROJECT_DIR/.claude/skills/prod-sql/run.sh" path/to/query.sql
```

- The SQL runs inside `BEGIN READ ONLY; … ROLLBACK;` on a session opened with `default_transaction_read_only=on`,
  so a stray write fails instead of applying. Do not add `BEGIN`, `COMMIT` or `ROLLBACK` yourself.
- The first error stops the run; check the column names with `--columns` instead of guessing.
- Useful tables: `sales.orders`, `sales.order_lines`, `sales.shipment_packages`, `sales.return_claims`,
  `integration.jobs`, `integration.job_attempts`, `integration.sync_cursors`, `integration.connection_sync_policies`,
  `integration.platform_connections`, `ops.operational_issues`, `sales.marketplace_finance_transactions`.
- Customer data (names, addresses, phones) stays on the server: select counts, ids and states, not personal fields.
