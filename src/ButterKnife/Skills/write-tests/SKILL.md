---
name: write-tests
description: Add tests in the project's existing style that cover the behaviour that matters
---
# Write tests

1. Find the test setup: the test folder, the framework (xUnit, NUnit, pytest, Jest, Vitest, Go testing, cargo test …)
   and the command that runs it. Read one or two existing test files for naming, structure, helpers and fakes.
2. Decide what to cover: the main success case, edge cases (empty, missing, boundary values, unusual input) and the
   error cases the code handles on purpose. For a bug fix, write the test that reproduces the bug first.
3. Put the tests where the project puts them, named the way it names them, using its existing helpers instead of
   writing new ones. Test behaviour through the public surface, not private details.
4. Keep each test small, independent and deterministic: no real network, no sleeps, no dependence on test order;
   temporary files go in a temp folder that the test cleans up.
5. Run the new tests with `run_command`, then the whole suite.

Report what the tests cover and anything worth testing that you left out, and why.
