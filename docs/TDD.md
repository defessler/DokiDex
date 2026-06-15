# Technical Design Document — Fully-Local Agentic Coding Stack

| | |
|---|---|
| **Project** | DokiDex infrastructure |
| **Status** | Approved |
| **Author** | Doug Fessler (with Claude) |
| **Date** | 2026-06-12 |
| **Target hardware** | RTX 5090 (32GB VRAM) · 64GB DDR5 · i9-14900KS · Windows 11 |

> **Note:** this is the original *design* doc (foundation: inference + harness + search). The
> system has since grown image/video/music/edit generation, speech (TTS/STT), a persistent-memory
> MCP, and a WPF control panel. For how the **complete as-built system** works, see
> **[how-it-works.md](how-it-works.md)**.

---

## 1. Summary

Build an AI agentic coding setup that gets as close as possible to Claude Code / Codex / Copilot quality while running **entirely on local hardware**. No cloud AI services at runtime; web *search* is permitted. The stack assembles best-in-class open-source components — llama.cpp for inference, llama-swap for model routing, Crush/OpenCode as the agent harness — and closes the remaining quality gap with measurement-driven workflow engineering (golden-task evals, per-repo conventions, verification loops).

## 2. Background & motivation

Frontier coding agents (Claude Code, Codex, Copilot) deliver enormous productivity but require sending code to third-party clouds and ongoing subscription cost. Open-weight models and open-source harnesses have matured to the point where a high-end consumer GPU can host a genuinely useful autonomous coding agent. This project stands that up, with a path to (a) swap in better open models as they release and (b) eventually build a custom harness ("DokiCode proper") on top of the experience gained.

## 3. Goals

1. A Claude Code-style **terminal agent** working against local models: file read/edit, shell execution, permissions, MCP tools.
2. **Two-tier model serving**: a fast everyday model fully in VRAM and a higher-quality sparse-MoE model using CPU-offloaded experts, selectable per task behind one endpoint.
3. **Web search for the agent** without any AI cloud dependency.
4. A **golden-task eval suite** so every model/config/harness change is measured, not vibed.
5. (Later phase) **Copilot-style tab autocomplete** in VS Code from a small local FIM model.

### Non-goals

- Matching frontier-model quality on long-horizon, ambiguous, multi-file tasks (hardware-capped; see §4).
- Building a custom harness now (future option, §7 Phase 6).
- Multi-user serving, quantization research, training/fine-tuning (future option).

## 4. Constraints & expectations

**Hard constraint:** 32GB VRAM + 64GB system RAM bounds model choice. Sparse MoE models with small active-parameter counts are the key to quality within this budget.

**Expectation setting** (June 2026 landscape):

| Capability | Best local on this rig vs frontier |
|---|---|
| Tab autocomplete | Near parity — small FIM models are excellent |
| Routine, well-scoped agent tasks | ~80–90% of frontier quality |
| Long-horizon / multi-file / ambiguous tasks | Behind by ~15–25 pts on SWE-bench-class benchmarks |
| Tool-calling reliability | Within a few % given native chat templates |

Claude Code's quality is roughly 80% model, 20% harness. The harness layer is replicable 1:1 with open source; the model layer is capped but rises monthly as open weights improve — the design makes model swaps a config change plus an eval run.

## 5. Architecture

```
┌────────────────────────────────────────────────────────┐
│ Terminal harness: Crush (primary candidate)            │
│   tools: read/edit/bash/grep · LSP · MCP · permissions │
└───────────────┬───────────────────────┬────────────────┘
                │ OpenAI-compatible API │ MCP (stdio)
┌───────────────▼──────────────┐  ┌─────▼──────────────────┐
│ llama-swap (model router)    │  │ web-search MCP server  │
│  ├─ agent model (GPU)        │  │ (DDG free / SearXNG)   │
│  ├─ big model (GPU+RAM MoE)  │  └────────────────────────┘
│  └─ FIM model (phase 5)      │
│ each = llama.cpp llama-server│
└───────────────┬──────────────┘
        RTX 5090 32GB + 64GB DDR5
```

Every component is free, open source, actively maintained, and Windows-native. No Docker/WSL required (optional exception: SearXNG).

### Component decisions

| Layer | Choice | Why | Rejected alternatives |
|---|---|---|---|
| Inference engine | **llama.cpp `llama-server`**, native Windows CUDA build | Fastest single-user path; native tool-call templates (`--jinja`); prompt caching; MoE CPU offload | Ollama/LM Studio (wrapper latency/quirks, feature lag); vLLM/SGLang (WSL2-only, ~20–40% penalty, multi-user-oriented) |
| Model router | **llama-swap** | Single OpenAI endpoint, auto start/swap per model name, Go binary, Windows-native | Manual server juggling; Ollama model management |
| Harness | **Crush** primary, **OpenCode** challenger | Crush: Go binary, first-class Windows/PowerShell, MCP, LSP, permissions. OpenCode: richest features but Node/WSL-leaning + reported local-endpoint latency bugs | Aider (kept as fallback — diff-format editing tolerates weak models best, but less autonomous); OpenHands CLI (Python, Windows-weak); Goose (desktop-leaning) |
| Web search | **DuckDuckGo MCP** (keyless) → SearXNG if limiting | Zero infra, no API key, no AI cloud | Brave/Google APIs (keys, cloud reliance) |
| Autocomplete | **llama.vscode** + small FIM model | Purpose-built for llama.cpp `/infill`, sub-200ms | Continue.dev (heavier; fine as alternate) |

## 6. Detailed design

### 6.1 Inference layer

**Models** (candidates as of June 2026 — verify current best at install time against HF trending / r/LocalLLaMA / leaderboards; names are candidates, not gospel):

| Role | Candidate | Quant | Weights | Fit & expected speed |
|---|---|---|---|---|
| A. Daily driver | Qwen3-Coder-30B-A3B (or newest ~30B coder MoE) | Q4_K_M–Q6_K | ~19–25GB | Fully on GPU → 80–150 tok/s decode, 128k ctx with q8 KV |
| B. Heavy hitter | gpt-oss-120b (5.1B active, Apache-2.0) | native MXFP4 | ~61GB | Attention+KV on GPU, experts→RAM (`--n-cpu-moe`) → ~25–35 tok/s |
| B-alt | Qwen3-Coder-Next-80B-A3B / GLM-4.5-Air-class | Q4_K_M | ~45–60GB | Same offload pattern |
| C. FIM (phase 5) | Qwen2.5-Coder-7B-base (or newer small FIM coder) | Q8_0 | ~8GB | Coexists with A |

**Memory budget rule:** weights + KV cache + ~1.5GB overhead ≤ 32GB VRAM. KV at 128k ctx is ~6–12GB fp16 for these architectures → always run `--cache-type-k q8_0 --cache-type-v q8_0 --flash-attn`. If OOM: reduce ctx to 96k or step down a quant.

**llama-swap config sketch** (`serving/llama-swap.yaml`):

```yaml
models:
  "coder-fast":   # model A — default agent model
    cmd: llama-server -m models/qwen3-coder-30b-a3b-q4_k_m.gguf
         --port ${PORT} -ngl 99 -c 131072 --jinja
         --cache-type-k q8_0 --cache-type-v q8_0 --flash-attn
         --cache-reuse 256
  "coder-big":    # model B — opt-in for hard tasks
    cmd: llama-server -m models/gpt-oss-120b-mxfp4.gguf
         --port ${PORT} -ngl 99 --n-cpu-moe 24 -c 131072 --jinja
         --cache-type-k q8_0 --cache-type-v q8_0 --flash-attn --cache-reuse 256
```

Load-bearing flags: `--jinja` (native tool-call chat templates — critical for agent reliability), `--cache-reuse` (prompt cache across agent turns; each turn re-sends the whole conversation, this keeps turn latency ~constant), `--n-cpu-moe N` (tune so VRAM sits ~30GB).

### 6.2 Harness layer

Configure Crush and OpenCode against `http://localhost:8080/v1`, `coder-fast` default, `coder-big` selectable. Crush sketch (verify schema against current docs):

```json
{
  "providers": {
    "local": {
      "type": "openai", "base_url": "http://localhost:8080/v1", "api_key": "none",
      "models": [{ "id": "coder-fast", "context_window": 131072 },
                 { "id": "coder-big",  "context_window": 131072 }]
    }
  }
}
```

**Making local models behave** (most of perceived "harness quality"):

- **AGENTS.md in every working repo** — build/test commands, code style, "always run X before done." Single highest-leverage artifact.
- **Minimal enabled tools** — every extra MCP tool degrades open-model tool selection. Start with zero MCP; add search only in 6.3.
- **Low sampling temperature** (~0.1–0.3) for agent work.
- **One task per session**; compact before long sessions degrade.
- **Permissions**: file-write + bash on "ask" until trusted, then loosen per-project.

**Bake-off**: run 5 identical real tasks (from real repos, e.g. D4Scanner: "add CLI flag + test", "fix seeded bug", "explain subsystem") across Crush×A, Crush×B, OpenCode×A, OpenCode×B. Score completion-without-intervention, edit correctness, wall time. Daily driver chosen on data; recorded in `docs/decisions.md`.

### 6.3 Web search

- **Quick win**: keyless DuckDuckGo MCP server (stdio via `npx`/`uvx`) wired into the harness. Verify the currently best-maintained package at install time.
- **Robust option** (only if DDG limits): self-hosted SearXNG (Docker Desktop/WSL2) + searxng-MCP.
- Later, sparingly: fetch/readability MCP, GitHub MCP. Tool bloat hurts open models.

### 6.4 Quality engineering (the gap-closer)

1. **Golden-task eval suite** (`evals/`): 10–15 tasks from real repos. Each = clean git worktree + task prompt + objective pass check (build green / test passes). Headless runner (`crush run "<prompt>"`) writes a scorecard. Re-run on every model/quant/flag/harness change — never tune blind.
2. **Conventions library**: refine each repo's AGENTS.md from observed failures (wrong test command, hallucinated path → encode the correction).
3. **Workflow discipline**: plan-first prompting for big tasks; small reviewable diffs; "write the failing test first" framing — weak models gain disproportionately from externalized verification.
4. **Tool-call reliability**: if tool-call JSON misfires >~5% of turns → try higher quant, `--jinja` template override, or grammar-constrained output; re-measure.
5. **Model refresh cadence**: monthly leaderboard check; new candidate → eval suite → swap only on a win. The rig's ceiling rises for free.

### 6.5 Autocomplete (phase 5)

- Serve model C persistently alongside A via llama-swap `groups` or a dedicated `llama-server --port 8012` exposing `/infill`. VRAM: ~8GB (C) + ~22GB (A) fits.
- Editor: llama.vscode (or Continue.dev if better maintained at install time).
- Acceptance: inline completions with all cloud extensions disabled, Copilot-like latency (sub-~200ms).

## 7. Implementation roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| **0. Foundation** | Repo skeleton, this TDD committed, private GitHub remote; verify ~150GB free disk | Repo pushed; disk verified |
| **1. Inference** | Install llama.cpp (CUDA build) + llama-swap; download models A + one B; tune flags | Tool-call `curl` returns valid `tool_calls` from both models; decode ≥60 tok/s (A), ≥20 tok/s (B); numbers recorded |
| **2. Harness** | Install Crush + OpenCode, wire to endpoint, AGENTS.md, permission config, bake-off | "Add CLI flag + passing test" completes on a real repo with ≤1 intervention; permission prompt fires on destructive ops; winner documented |
| **3. Quality** | Eval suite + runner, conventions library, reliability tuning | Scorecard runs headless; baseline ≥60% pass; subsequent changes ≥ baseline |
| **4. Search** | DDG MCP into harness | Agent answers a current-events dev question via live search; network monitor shows no AI-cloud traffic |
| **5. Autocomplete** | FIM model + llama.vscode | Local inline completions, cloud extensions disabled |
| **6. Future options** | Custom DokiCode harness (study Crush/OpenCode/Codex CLI internals; eval suite becomes its benchmark) · RAM 64→192GB (~$400, unlocks 200–250B-class MoEs) · LoRA fine-tuning on own traces (Unsloth) | Re-evaluated after Phase 3 data |

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Research staleness — model names/scores from June-2026 web research of mixed quality (some numbers provably off) | Bake-off + eval suite decide everything; §6.1 names are candidates to verify at install time |
| Tool-call flakiness on open models | `--jinja` templates, low temp, quant bump, grammar constraints; Aider fallback avoids tool-call JSON entirely |
| VRAM OOM at long context | q8 KV cache, ctx reduction playbook (§6.1) |
| Windows-specific harness bugs | Crush chosen for Windows-native support; OpenCode challenger; WSL2 escape hatch |
| Disk exhaustion (~150GB of GGUFs) | Check free space before each download; `models/` is git-ignored |

## 9. Provenance note

Component and model recommendations synthesized 2026-06-12 from parallel web research (models / harnesses / serving stack) cross-checked against known-stable facts. Anything marked "verify at install time" had conflicting or unverifiable sources; the eval suite (§6.4) exists precisely so that no such claim is trusted without local measurement.
