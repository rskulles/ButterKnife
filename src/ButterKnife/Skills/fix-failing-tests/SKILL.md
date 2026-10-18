---
name: fix-failing-tests
description: Make a failing test suite or build pass by fixing the real problem
---
# Fix failing tests or a broken build

1. Find the test and build commands: AGENTS.md or CLAUDE.md, README, package.json scripts, Makefile, CI workflow files
   under .github/workflows. Run the build first, then the tests, with `run_command`.
2. Read the first failure carefully: the test name, the assertion, expected versus actual, the file and line.
   Later failures are often caused by the first one.
3. Run only that test when the framework allows it (for example `dotnet test --filter`, `pytest path::name`,
   `npm test -- -t name`, `go test -run Name`, `cargo test name`) so each try is fast.
4. Decide which side is wrong: the code or the test. Read both. Change the test only when it is clearly out of date
   with intended behaviour, and say so.
5. Fix, rerun that test, then rerun the whole suite to make sure nothing else broke.

Never delete, skip or weaken a test just to make it pass, and never change the build configuration to hide warnings,
unless the user asks. Report which tests failed, why, and what you changed.
