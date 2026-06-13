# DokiCode

Fully-local AI agentic coding infrastructure — a Claude Code / Codex / Copilot-class setup that runs entirely on my own hardware (RTX 5090 / 32GB VRAM, 64GB DDR5, Windows 11). No cloud AI services at runtime; web search allowed.

**Design doc:** [docs/TDD.md](docs/TDD.md) · **Friendly ELI5 explainer:** [docs/wiki/Home.md](docs/wiki/Home.md)

## Run it

```powershell
.\setup.ps1            # one-time: prereqs + deploy configs (chat/code)
.\setup.ps1 -Media     #   ...plus install image+video gen (SwarmUI + models, headless)

.\doki.ps1 up          # chat + code      → llama-swap on :8080
.\doki.ps1 up coexist  #   + autocomplete → FIM on :8012
.\doki.ps1 up media    # image + video    → SwarmUI on :7801
.\doki.ps1 status      # what's running + health      .\doki.ps1 down
```

GPU modes are mutually exclusive on 32GB, so `doki` switches between the LLM and the image/video server. The three things you launch yourself: the CLI (**Crush**), the chat app (**Chatbox**), and the editor (**llama.vscode**).

## Layout

| Path | Purpose |
|---|---|
| `doki.ps1` | Native control plane — start/stop/status the stack (no Docker) |
| `setup.ps1` | One-command bootstrap (prereqs, config deploy, `-Media` = SwarmUI + models) |
| `docs/` | Design doc, decision log, benchmarks, ELI5 wiki |
| `serving/` | llama.cpp / llama-swap configs and launch scripts |
| `harness/` | Crush / OpenCode / Chatbox / llama.vscode configs, AGENTS.md template |
| `evals/` | Golden-task eval suite, runner, scorecards |
| `media/` | SwarmUI + ComfyUI + image/video model weights — git-ignored |
| `models/` | GGUF weights — git-ignored, local only |

## Stack at a glance

- **Inference:** llama.cpp `llama-server` (native Windows CUDA) behind **llama-swap** (one OpenAI-compatible endpoint, multiple models)
- **Models:** ~30B coder MoE fully on GPU (fast daily driver) + ~120B sparse MoE with CPU-offloaded experts (heavy hitter)
- **Code:** Crush (daily driver, bake-off winner) — OpenCode / Claw Code challengers
- **Chat:** Chatbox → local endpoint · **Autocomplete:** small FIM model + llama.vscode
- **Search:** keyless DuckDuckGo MCP
- **Image + video:** SwarmUI (ComfyUI engine) — Z-Image Turbo + Wan 2.1, unfiltered, installed headlessly

## Status

**Complete and verified across the board (2026-06-13)** — fully local, one command to run:

- **Inference:** llama-swap `:8080` — `coder-fast` (265 tok/s), `coder-big`, `coder-fast-lite`; clean native tool calls.
- **Code:** Crush v0.76, **91%** on the 11-task golden suite (`docs/scorecards/`). Claw Code bake-off'd → rejected (45%, flaky tool calls).
- **Chat:** Chatbox → `:8080`. **Autocomplete:** Qwen2.5-Coder-3B FIM on `:8012` (live `/infill` verified).
- **Web search:** keyless DuckDuckGo MCP — no AI-cloud traffic.
- **Image + video:** SwarmUI/ComfyUI installed 100% headlessly — **Z-Image Turbo** (1024² in 54s) + **Wan 2.1** (832×480 clip in 25s), both uncensored & verified live.
- **Control plane:** `doki up/down/status/restart/logs` with agent / coexist / media profiles; one-command `setup.ps1`.
- **Model refresh (eval-gated):** Nemotron-Cascade-2 (45%) and Qwen3-Coder-Next-REAP (broken tool-calls) both lost — Qwen3-Coder-30B confirmed the best 32GB fit by measurement.

See `docs/benchmarks.md` (measurements), `docs/decisions.md` (every call + the eval gates), `docs/streamlined-setup-design.md` (control plane + media), and TDD §7 (roadmap).
