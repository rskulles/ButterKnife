---
name: security-review
description: Look for security problems in code or changes and report them with fixes
---
# Security review

1. Decide the scope: the changes (`git diff`) or a feature the user names. Find where untrusted input enters: HTTP
   handlers, form and query parameters, file uploads, command-line arguments, environment variables, files and data
   from other systems, model output.
2. Follow that input with `grep` and `read_file` to where it is used and check for:
   - injection: SQL built by string concatenation, shell commands with user text, HTML without escaping, template
     injection, unsafe deserialisation;
   - access control: endpoints or actions missing an authorisation check, checks done only in the UI;
   - file system: paths built from input without confinement (`..`), unsafe temporary files;
   - secrets: keys or passwords in code, configuration or logs;
   - crypto and sessions: home-made crypto, weak hashing of passwords, missing expiry, cookies without HttpOnly or Secure;
   - dependencies: known-vulnerable or unmaintained packages when the project has an audit command.
3. Confirm each finding by reading the code path end to end; do not report guesses as facts.

Report findings ordered by severity, each with the file and line, how it could be exploited and the fix. Do not edit
files unless the user asks you to fix something.
