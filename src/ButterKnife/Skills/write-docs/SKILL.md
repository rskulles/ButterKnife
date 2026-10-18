---
name: write-docs
description: Write or update documentation (README, guides, code comments) that matches what the code actually does
---
# Write documentation

1. Read the existing docs first and keep their voice, structure and formatting. Find out who reads them: users
   (how to install and use it) or developers (how it works and how to change it).
2. Check every statement against the code: commands by running them where it is safe, option names and defaults by
   reading where they are defined, paths with `list_files`. Do not document behaviour you have not confirmed.
3. Write for the reader's task: start with what they need first, use short paragraphs, exact commands in code blocks,
   and real examples from this project.
4. Update docs that the change makes wrong: README sections, configuration tables, comments above changed code,
   changelogs if the project keeps one.
5. For code comments, explain why, not what the next line does; keep them next to the code they explain.

Summarise which documents you changed and anything you could not verify.
