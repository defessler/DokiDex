# Working effectively with the local agent

Local models are more literal and less self-correcting than frontier models.
These practices recover a large share of the gap. Encode repo-specific versions
of them into each repo's AGENTS.md (template: `harness/AGENTS.md`).

## Task framing

1. **One task per session.** Start a fresh session per task; long sessions degrade
   as context fills. Compact (or restart) before ~60% context use.
2. **Externalize verification.** Phrase tasks as "make X pass" / "output must be Y",
   not "improve X". The model is dramatically more reliable when the finish line
   is checkable: failing test, exact CLI output, build green.
3. **Plan-first for big tasks.** For multi-file work, first ask: "Read the relevant
   code and write a numbered plan; don't edit yet." Review, then say "do step 1-2".
   Small models follow good plans much better than they invent them.
4. **Small diffs.** Ask for the smallest change that works; review; iterate. Big
   speculative refactors are where local models fall apart.

## Model selection

- `coder-fast` (default): everything routine — edits, tests, bugfixes, explanations.
- `coder-big`: opt-in for gnarly reasoning (concurrency bugs, architectural choices,
  ambiguous specs). First long-context turn is slow (~155 tok/s prefill); subsequent
  turns reuse the prompt cache.

## Session hygiene

- Always work in a git repo with committed state. Headless `run` modes auto-execute
  tools (measured — see decisions.md); git is the undo button.
- Keep MCP tools minimal: every enabled tool dilutes tool-selection accuracy.
- Sampling: harness defaults are fine; if tool calls get flaky, lower temperature
  in the harness model options before blaming the model.

## Continuous improvement loop

1. Notice a repeated failure (wrong test command, hallucinated path, skipped step).
2. Encode the correction in that repo's AGENTS.md (one line, imperative).
3. If it's stack-wide, add a golden task to `evals/tasks/` that catches it.
4. Re-run `evals/run-suite.ps1` after ANY change to model/quant/flags/harness;
   compare to `docs/scorecards/`. Never tune blind.

## Model refresh cadence (monthly)

1. Check HF trending + r/LocalLLaMA + SWE-bench/Aider leaderboards for new
   open-weight coder models that fit 32GB VRAM (or RAM-offloaded MoE ≤ ~60GB).
2. Download candidate quant, add to `serving/llama-swap.yaml` as `coder-candidate`.
3. `evals/run-suite.ps1 -Harness crush -Model coder-candidate` — swap defaults only
   if it beats the incumbent scorecard.
