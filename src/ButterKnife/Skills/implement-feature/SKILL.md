---
name: implement-feature
description: Add a feature or make a behaviour change end to end, following the project's existing patterns
---
# Implement a feature

1. Restate the goal in one sentence. If something essential is ambiguous (what the user sees, which file format, which
   behaviour wins), ask one short question before writing code.
2. Find the closest existing example of the same kind of thing (another endpoint, page, command, model, test) with
   `grep` and `list_files`, and read it. New code should look like it.
3. Make a short plan: the files to change and what changes in each. For more than three files, tell the user the plan
   before editing.
4. Make the changes with `edit_file`, one focused edit at a time. Copy `old_string` exactly from `read_file` output.
   Use `write_file` only for new files.
5. Wire everything up: registrations, routes, exports, configuration, migrations. Search for how the example from
   step 2 is registered and do the same.
6. Add or update tests for the new behaviour (see the write-tests skill).
7. Build and run the tests with `run_command`. Fix what fails, including warnings the project treats as errors.

Finish with a short summary: what changed, in which files, how you verified it, and anything left for the user to do or
check (for example a manual step, a setting, or a case you could not test).
