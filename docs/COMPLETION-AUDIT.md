# DokiDex — 6-Phase Completion Audit

> **Method:** multi-agent workflow (`dokidex-completion-audit`) — 6 read-only verifiers (one per phase) run in parallel against the live repo + running services, every fail/uncertain finding adversarially re-checked by an independent agent, then synthesized. 12 agents, ~350k tokens, ~3.5 min.

> **Resolution (2026-06-12):** the audit found the plan **functionally complete** with 5 doc/git-hygiene gaps. All 5 were closed in the same commit that adds this file:
> 1. Phase-0 disk measurement → recorded in `wiki/12-benchmarks.md` ✅
> 2. Phase-4 live-search proof → re-run (Node.js LTS query) + recorded, MCP wiring committed ✅
> 3. Phase-5 coexistence VRAM (27.6GB) → recorded in `wiki/12-benchmarks.md` ✅
> 4. README status text → updated to Phases 0–5 ✅
> 5. Working tree / uncommitted Phase 4–5 work → committed ✅

---

**OVERALL VERDICT:** ✅ **Functionally complete** — all six phases' core capabilities are built and verified live (inference, harness, evals, web-search wiring, FIM autocomplete). The remaining items were documentation/status-text and git-hygiene only — no functional capability was missing.

## Phase status

| Phase | Core capability working? | Notes |
|---|---|---|
| **0 — Disk/bootstrap** | ✅ Yes | Repo bootstrapped; models on D:. Disk-free now recorded (221GB at install, 142GB after downloads). |
| **1 — Inference layer** | ✅ Yes | llama.cpp b9616 CUDA + llama-swap on :8080 serve all 3 model IDs; both models on disk at expected sizes; YAML carries all reliability flags; benchmarks meet targets. |
| **2 — Harness** | ✅ Yes | Crush v0.76.0 (daily driver) + OpenCode 1.17.4 installed; both global configs wire `local` → :8080 with coder-fast/coder-big; 8-run bake-off documented. |
| **3 — Golden-task evals** | ✅ Yes | 3 runner scripts + 11 task folders (task.json+check.ps1) + C# sandbox-seed; scorecard **10/11 (91%)** ≥ 60% gate; validate-tasks anti-cheat guard sound. |
| **4 — Web-search MCP** | ✅ Yes | duckduckgo-mcp-server via uvx in both global + template Crush configs; uvx 0.11.21 runnable. Live search verified (Node.js 24 LTS/Krypton); no AI-cloud traffic. |
| **5 — FIM autocomplete** | ✅ Yes | 3B Q8 model on disk; start-fim.ps1 serves :8012; live `/infill` returns correct output; coexistence profile + llama-vscode 0.0.48 + VS Code endpoint wired; 27.6GB coexistence measured. |

## Evidence highlights

- **Inference throughput (live):** coder-fast **265.4 tok/s** decode (target ≥60, 4.4×); coder-big **27 tok/s** (target ≥20); both with clean `tool_calls` JSON.
- **Endpoint live:** `curl http://127.0.0.1:8080/v1/models` → `coder-big`, `coder-fast`, `coder-fast-lite`.
- **Eval scorecard:** **10 / 11 (91%)** ≥ 60% gate; t8-extract-helper the sole ❌ (tripped by hidden tests → anti-cheat guard works).
- **Web search (live):** Crush+coder-fast-lite+websearch MCP answered "latest Node.js LTS" → **24 / Krypton**; `Get-NetTCPConnection` on inference PIDs → **zero** non-local connections.
- **FIM autocomplete (live):** `:8012/infill` returned `return fibonacci(n-1) + fibonacci(n-2)` at **292 tok/s**.
- **Coexistence (live):** FIM (3B Q8) + coder-fast-lite (64k) both resident at **27.6GB / 32.6GB**.
- **Repo hygiene:** `git ls-files` shows **zero** tracked `.gguf/.exe/.dll`/weights; `models/` correctly ignored; structure matches TDD.

## What "done" means here

This delivers the planned ceiling for the hardware: a Claude Code-style local terminal agent (Crush) on a two-tier local model stack, a measured eval harness to gate every future change, live web search with no AI-cloud dependency, and Copilot-style local autocomplete. Quality vs. frontier is model-bound (see TDD §4) — the stack is built so a better open model is a config swap + one eval run away. Future options (custom harness, RAM upgrade to run 200B-class MoEs, LoRA on accepted traces) remain in TDD §7 Phase 6, intentionally out of scope.
