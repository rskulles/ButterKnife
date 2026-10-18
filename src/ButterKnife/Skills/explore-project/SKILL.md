---
name: explore-project
description: Get oriented in an unfamiliar project before answering questions about it or changing it
---
# Explore a project

Use this when the project is new to you, the user asks how something works, or you are about to change code you have not read.

1. `list_files` with no arguments to see the top level. Note the language, the build system and the folders that matter.
2. Read the files that explain the project, in this order, skipping any that do not exist:
   AGENTS.md or CLAUDE.md, README.md, then the build or package file (package.json, *.csproj or *.sln, pyproject.toml,
   Cargo.toml, go.mod, pom.xml, build.gradle, Makefile).
3. Find the entry points: `grep` for `main(`, `app.listen`, `if __name__`, `Program.cs`, routes or CLI definitions.
4. Find the tests: `list_files` with a pattern such as `**/*test*` and read one test file to learn the test style.
5. When the question is about one feature, `grep` for its key words (a function name, a UI string, an error message)
   and read the files that come up, following calls one level deep.

Then answer in a few short paragraphs: what the project is, how it is laid out (main folders and what lives in each),
how to build and test it (exact commands), and where the code for the user's question lives (file paths and names).
Do not list every file. Do not change anything while exploring.
