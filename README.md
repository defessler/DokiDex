# DokiCode

Fully-local AI agentic coding infrastructure — a Claude Code / Codex / Copilot-class setup that runs entirely on my own hardware (RTX 5090 / 32GB VRAM, 64GB DDR5, Windows 11). No cloud AI services at runtime; web search allowed.

**Design doc:** [docs/TDD.md](docs/TDD.md)

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

Phase 0 — repo bootstrapped, design doc committed. See TDD §7 for the phased roadmap.
