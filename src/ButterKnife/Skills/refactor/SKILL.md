---
name: refactor
description: Restructure code without changing what it does, in small verified steps
---
# Refactor safely

1. Make sure there is a safety net: find and run the tests that cover the code. If there are none for the part you
   are changing, tell the user and offer to add some first.
2. Find every use of what you are changing with `grep` (names, string references, reflection, configuration, docs) so
   nothing is missed.
3. Change one thing at a time: rename, extract, move or simplify, then build and run the tests before the next step.
4. Keep behaviour identical: same inputs give the same outputs, errors and side effects. Do not mix in bug fixes or
   new features; note them for the user instead.
5. Follow the project's existing style and formatter; run it if the project has one.

Summarise the structural change, the files touched, and the test results.
