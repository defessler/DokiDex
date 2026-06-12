# AGENTS.md

## Project

Small C# console app (`src/App`) with xunit tests (`tests/App.Tests`).

## Commands

- Build: `dotnet build`
- Test: `dotnet test`
- Run: `dotnet run --project src/App -- <args>`

## Rules

- Always run `dotnet test` before declaring a task done.
- Make the smallest change that solves the task. Do not refactor unrelated code.
- Never modify existing tests unless the task explicitly asks for it.
