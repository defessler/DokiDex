# DokiCode

Fully-local AI agentic coding infrastructure — a Claude Code / Codex / Copilot-class setup that runs entirely on my own hardware (RTX 5090 / 32GB VRAM, 64GB DDR5, Windows 11). No cloud AI services at runtime; web search allowed.

**Design doc:** [docs/TDD.md](docs/TDD.md) · **Friendly ELI5 explainer:** [docs/wiki/Home.md](docs/wiki/Home.md)

## Layout

| Path | Purpose |
|---|---|
| `docs/` | Technical design doc, decision log, benchmark records |
| `serving/` | llama.cpp / llama-swap configs and launch scripts |
| `harness/` | Crush / OpenCode configs, shared AGENTS.md templates |
| `evals/` | Golden-task eval suite, runner, scorecards |
| `models/` | GGUF weights — git-ignored, local only |

## Stack at a glance

- **Inference:** llama.cpp `llama-server` (native Windows CUDA) behind **llama-swap** (one OpenAI-compatible endpoint, multiple models)
- **Models:** ~30B coder MoE fully on GPU (fast daily driver) + ~120B sparse MoE with CPU-offloaded experts (heavy hitter)
- **Harness:** Crush (primary) vs OpenCode (challenger) — winner picked by eval bake-off
- **Search:** keyless DuckDuckGo MCP (SearXNG later if needed)
- **Autocomplete (later):** small FIM model + llama.vscode

## Status

**Phases 0–5 complete and verified (2026-06-12).** Fully working local stack:

- **Inference:** llama.cpp b9616 (CUDA) + llama-swap on `:8080` — `coder-fast` (265 tok/s), `coder-big`, `coder-fast-lite`; clean native tool calls.
- **Harness:** Crush v0.76 (daily driver, bake-off winner) + OpenCode, wired to the local endpoint.
- **Evals:** 11-task golden suite, **91% baseline** (`docs/scorecards/`).
- **Web search:** keyless DuckDuckGo MCP — verified live, no AI-cloud traffic.
- **Autocomplete:** Qwen2.5-Coder-3B FIM on `:8012` + llama.vscode; coexists with the agent at ~27.6GB VRAM.

See `docs/benchmarks.md` for measurements, `docs/decisions.md` for the harness bake-off, `docs/COMPLETION-AUDIT.md` for the multi-agent verification, and TDD §7 for the roadmap.
