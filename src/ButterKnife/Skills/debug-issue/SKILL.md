---
name: debug-issue
description: Find and fix the cause of a bug, crash or wrong behaviour instead of guessing
---
# Debug an issue

1. Get the facts: the exact error message, stack trace or wrong output, and how to trigger it. If the user gave a
   command, run it with `run_command` to see the failure yourself.
2. Locate it: `grep` for the error text, the function names in the stack trace, or the identifiers involved, then read
   the code around each hit. Follow the data back to where the wrong value comes from.
3. Form one hypothesis at a time and check it by reading code or running something small (a single test, a one-line
   script, a command with extra output). Say what you expect to see before you run it.
4. When a hypothesis is confirmed, fix the cause, not the symptom: no swallowing exceptions, no special-casing the one
   failing input unless that input really is special.
5. Prove the fix: rerun the command or test that failed, then the related tests. If there was no test for this bug,
   add one that fails without the fix.

If two hypotheses fail, stop and summarise what you know and what you ruled out before trying more. Report the cause in
one or two sentences, the fix, and how you verified it.
