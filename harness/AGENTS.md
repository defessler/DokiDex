# AGENTS.md template

<!-- Copy into each working repo and fill in. Keep it SHORT — every line costs
     context on every turn. Encode only what the agent gets wrong without it. -->

## Project

One sentence: what this codebase is.

## Commands

- Build: `dotnet build`
- Test: `dotnet test`
- Run: `dotnet run --project src/App`

## Rules

- Always run the test command before declaring a task done; paste the result.
- Make the smallest change that solves the task. Do not refactor unrelated code.
- Never invent file paths — list the directory if unsure.
- Follow the existing code style of the file you are editing.

## Memory

A persistent `memory` MCP is available and survives across sessions — use it:

- **Starting a non-trivial task:** `memory_search` the relevant keywords first to recall prior
  decisions, gotchas, and context for this project.
- **On a decision, a gotcha, or a non-obvious fact:** `memory_save` it (one fact per note, with
  comma-separated `tags`) so a future session doesn't relearn it. Don't save what's already
  obvious from the code or git history.
