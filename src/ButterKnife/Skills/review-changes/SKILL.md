---
name: review-changes
description: Review uncommitted or recent changes for bugs and risks, and report findings without editing
---
# Review changes

1. See what changed: `run_command` with `git status` and `git diff` (add `--staged` for staged changes, or
   `git diff main...HEAD` for a branch, or `git show` for the last commit).
2. Read each changed file around the edits, not just the diff lines, so you understand the context.
3. Look for, in this order: wrong behaviour and logic errors, unhandled errors and edge cases (null, empty, missing
   files, concurrency, cancellation), security problems (injection, secrets, unchecked input, paths outside allowed
   folders), resource leaks, missing or wrong tests, and code that ignores the project's conventions.
4. Check claims against the code: if a comment or name says one thing and the code does another, that is a finding.

Report findings ordered by severity, each with the file and line, what goes wrong and a concrete fix. Say plainly when
you found nothing serious. Do not edit files during a review unless the user asks you to fix something.
