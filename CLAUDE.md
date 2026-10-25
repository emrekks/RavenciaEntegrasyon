# Ravencia Marketplace Hub

Reply to the user in Turkish: answers, summaries, closing reports and tool descriptions alike.

## Stack
- .NET 10 API and Worker (`src/MarketplaceHub.*`), EF Core with Npgsql on PostgreSQL. Migrations live in
  `src/MarketplaceHub.Infrastructure/Persistence/Migrations`.
- Web: `src/MarketplaceHub.Web`, React 19, TypeScript, Vite, TanStack Query, vitest.
- Live: EC2 `63.180.140.51`, Docker containers `marketplacehub-api-1`, `-worker-1`, `-caddy-1`, `-postgres-1`.

## Checks
- Backend: `dotnet build MarketplaceHub.sln`, `dotnet test tests/MarketplaceHub.Application.Tests`,
  `dotnet format MarketplaceHub.sln --verify-no-changes --include <files>`.
- Web (in `src/MarketplaceHub.Web`): `npm run typecheck` (it is `tsc -b`; `tsc -p .` misses project references),
  `npx vitest run <dir>`, `npm run lint`, `npm run build`, `npm run check:bundle`.
- Run the tests again after merging `origin/main`: another session's commits can break a test that passed before.

## Shipping (`/ship`)
- Commit messages in English, no attribution lines.
- Push to `codex/<topic>`, wait for Validate, then push the same sha to `main` only when
  `git merge-base --is-ancestor origin/main <sha>`. Never force-push; when `main` moved, merge `origin/main` first.
- Watch the live images (`docker inspect`) reach the sha before calling it live.
- The host stops at 00:30 Istanbul time and starts at 09:00; a push validated while it is stopped is shipped by the 09:20 scheduled run.
- Several sessions commit in this checkout and on `main` at once. Work in a worktree under `.claude/worktrees/` and
  never commit, revert or stash another session's uncommitted files.

## Live server
- Read-only, always: SQL inside `BEGIN READ ONLY … ROLLBACK` (`/prod-sql`), `docker inspect` and `docker logs`.
  Never restart, edit or write anything on the server.
- A fix is not fixed until it is checked on live. Numbers shown to the user (reports, costs) are compared with
  read-only SQL first; a wrong number on screen is worse than a missing one.

## Domain notes
- Npgsql `timestamptz` accepts offset 0 only: `.ToUniversalTime()` before saving or querying.
- Hepsiburada writes Istanbul local times; its query times are Istanbul local too (`HepsiburadaJsonMapper.QueryTime`).
- Never infer a marketplace state from a missing field (no invoice link is not "not invoiced"); unknown stays unknown.
- Trendyol cross-border storefront orders (SA, AZ, BG, RO, GR, AE) ship to Trendyol's Istanbul export depot; they
  count in their storefront's country, not in Istanbul.

## UI
- Controls 36 px high; radii 7 / 12 / 14 px; labels in Title Case.
- The global `button` rule sets `min-height: 44px`; reset it (`min-height: 0`) on compact controls.
- Check UI in your own browser tab and close it afterwards; never type credentials or passwords into a form.
