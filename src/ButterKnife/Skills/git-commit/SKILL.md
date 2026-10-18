---
name: git-commit
description: Commit the current changes with a message in the repository's own style
---
# Commit changes

1. `run_command` with `git status` and `git diff` to see exactly what will be committed. Look for files that should
   not be committed: secrets, local configuration, build output, large binaries, debug leftovers. Ask about anything
   doubtful instead of committing it.
2. `git log --oneline -10` to learn the message style (tense, prefixes, length).
3. Stage only what belongs together, for example `git add path/one path/two`. If the changes cover unrelated
   things, ask whether to make separate commits.
4. Commit with a message that says what changed and why, in the repository's style: a short summary line, then a blank
   line and detail if needed. Use `git commit -m "summary" -m "detail"` (quote carefully).
5. Run `git log --oneline -1` to confirm.

Never push, force-push, rebase, amend or change branches unless the user asks. Never skip hooks with --no-verify.
