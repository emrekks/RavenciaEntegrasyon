---
name: ship
description: Ships the current commit to live - pushes it to a codex/<topic> branch, waits for Validate, fast-forwards main and watches the live images reach it. Use after the change is committed and its local checks pass.
disable-model-invocation: true
argument-hint: "[codex branch topic, defaults to the current branch name]"
---

# Ship to live

Run from the worktree (or checkout) whose `HEAD` is the commit to ship. The working tree must be clean of your own
changes: commit first (English message, no attribution lines).

1. If `origin/main` moved since you branched, merge it (`git fetch origin && git merge --no-edit origin/main`) and run
   the affected tests again: other sessions commit to `main` all the time and a merge can break a test.
2. Start the script in the background (it waits for CI and the deploy, 15 to 40 minutes):

   ```bash
   bash "$CLAUDE_PROJECT_DIR/.claude/skills/ship/ship.sh" <topic>
   ```

   With no topic the current branch name is used (a `codex/` prefix is added when missing).
3. Read its result lines when it ends:
   - `validate=0` / `validate=1`: CI result; on failure the failing tests are printed. Fix, commit, ship again.
   - `pushed`: main now points at the commit. `not pushed`: main moved meanwhile (its last commits are printed):
     merge `origin/main`, re-test, ship again. Never force-push.
   - `live: …`: the edge, api and worker images all run the commit. Without it the deploy has not finished; while the
     host is stopped (00:30-09:00 Istanbul time) the 09:20 scheduled run ships it.
4. Only after `live:` check the change on the live panel (your own browser tab) before calling it done.

To stop a running ship, stop its background task; nothing is pushed to `main` before Validate is green.
