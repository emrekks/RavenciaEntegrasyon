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
2. Start the script in the background from the worktree's own copy (the main checkout's copy can be old):

   ```bash
   bash "$(git rev-parse --show-toplevel)/.claude/skills/ship/ship.sh" <topic>
   ```

   With no topic the current branch name is used (a `codex/` prefix is added when missing). It validates the commit,
   then queues for `main` behind a lock shared by every worktree (`<git-common-dir>/ship.lock`, taken over after
   60 minutes), so only one ship moves `main` at a time. If `main` moved, it merges `origin/main` while holding the
   lock, validates that merge and pushes it. This takes 15 to 60 minutes when other ships are queued.
3. Read its result lines when it ends:
   - `validate=0` / `validate=1`: CI result; on failure the failing tests are printed. Fix, commit, ship again.
   - `waiting for ship lock: <branch> <time>`: another ship is ahead; nothing to do.
   - `main moved: merging origin/main` and a new `sha=`: the merge commit is what ships; it is validated again.
   - `pushed`: main now points at the commit. `not pushed: merge conflict with main`: resolve it, re-test, ship
     again. `not pushed: main moved outside ship`: someone pushed to main without this script; ship again.
     Never force-push.
   - `live: …`: the edge, api and worker images all run the commit. Without it the deploy has not finished; while the
     host is stopped (00:30-09:00 Istanbul time) the 09:20 scheduled run ships it.
4. Only after `live:` check the change on the live panel (your own browser tab) before calling it done.

To stop a running ship, stop its background task; nothing is pushed to `main` before Validate is green.
