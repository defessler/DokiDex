# Benchmarks & measurements

Hardware: RTX 5090 (32GB) · 64GB DDR5 · i9-14900KS · Windows 11
Stack: llama.cpp b9616 (CUDA 13.3 build) · llama-swap v224 · driver 610.47

## Phase 0 — foundation (2026-06-12)

| Check | Result | Status |
|---|---|---|
| Disk free on D: at install time (pre-download) | 221GB free / 3726GB total | ✅ ≥150GB target met |
| Disk free on D: after model downloads (~84GB pulled) | 142GB free / 3726GB total | ✅ ample headroom |
| GPU / driver | RTX 5090 32GB, driver 610.47 (Blackwell needs 570+) | ✅ |

## Phase 1 — serving acceptance (2026-06-12)

### coder-fast — Qwen3-Coder-30B-A3B-Instruct UD-Q4_K_XL (17.7GB, fully on GPU)

Config: `-ngl 99 -c 131072 --flash-attn on --cache-type-k/v q8_0 --cache-reuse 256 --jinja`

| Metric | Result | Target | Status |
|---|---|---|---|
| Native tool call (`tool_calls` JSON) | clean, correct name+args | valid JSON | ✅ |
| Cold turn incl. model load | 6.6s | — | ✅ |
| Decode @ short ctx | **265.4 tok/s** | ≥60 | ✅ 4.4× |
| Decode @ 14.5k ctx | 166.4 tok/s | — | ✅ |
| Prefill @ 14.5k-token prompt | **~6,970 tok/s** | — | ✅ (100k ctx ≈ ~14s cold; cached turns skip most) |
| VRAM with full 128k KV allocated | 30.3GB / 32.6GB | fit | ✅ (note: tight — see phase-5 note) |

**Phase-5 coexistence note:** at 128k ctx coder-fast leaves only ~2GB VRAM free. For concurrent FIM autocomplete, either drop coder-fast to 64k ctx (~3GB freed) or use a 1.5B-class FIM model (~2GB). Decide with measurements in phase 5.

### coder-big — gpt-oss-120b MXFP4 (60.8GB, experts→RAM via `--n-cpu-moe`)

Tuned config: `-ngl 99 --n-cpu-moe 22 -b 2048 -ub 2048 -c 131072 --flash-attn on --cache-type-k/v q8_0 --cache-reuse 256 --jinja`

| Metric | Result | Target | Status |
|---|---|---|---|
| Native tool call (`tool_calls` JSON) | clean, correct name+args | valid JSON | ✅ |
| Cold turn incl. 60GB model load | 62.4s (subsequent loads faster via page cache) | — | ✅ |
| Decode @ short ctx | **27 tok/s** | ≥20 | ✅ |
| Decode @ 7.3k ctx | 21.2 tok/s | — | ✅ |
| Prefill @ 7.3k-token prompt (warm) | ~155 tok/s | — | ⚠️ CPU-expert bound; `--cache-reuse` limits real turns to delta-prefill |
| VRAM | 31.2GB / 32.6GB | fit | ✅ (1.4GB margin — don't run other GPU loads alongside) |
| RAM working set | ~36GB (at ncmoe 24; ~32GB at 22) | ≤64GB | ✅ |

Tuning history: `--n-cpu-moe 24` → 116 tok/s prefill, 15 tok/s decode @7.3k. Going to `22` + `-b/-ub 2048` bought +34% prefill and decode over target. `21` would exceed VRAM.

**Usage guidance:** coder-fast is the daily driver (fast everything). coder-big is opt-in for hard reasoning tasks; its first long-context turn is slow (~155 tok/s prefill ≈ ~3 min for a fresh 30k context), then cached turns are fine. Both models pass native tool-calling.

**Phase 1 exit criteria: all met (2026-06-12).**

## Phase 3 — golden-task baseline (2026-06-12)

11-task suite (`evals/tasks/`), headless via Crush, all tasks pre-validated to fail unsolved.

| Combo | Score | Notes |
|---|---|---|
| **Crush × coder-fast** | **10/11 (91%)** | Only t8-extract-helper failed: hidden tests caught a behavior change during refactor — the known weak spot (semantic preservation) for ~30B local models. Avg 27.8s/task. |

Gate was ≥60% — **passed with margin**. Full detail: `docs/scorecards/2026-06-12-crush-coder-fast.md`.
Tool-call reliability: 0 malformed tool calls observed across 19 headless agent runs today.

## Phase 4 — web search MCP (2026-06-12)

Keyless DuckDuckGo MCP (`uvx duckduckgo-mcp-server`, uv 0.11.21) wired into Crush as a stdio MCP server (`mcp.websearch`).

| Check | Result | Status |
|---|---|---|
| Agent answers a current-events dev question via live search | Q: "latest Node.js LTS major + codename?" → agent called the search tool, replied **"24 (LTS)" / Krypton** (correct as of 2026-06) | ✅ |
| Ran in coexistence mode | via `coder-fast-lite` while the FIM server stayed loaded | ✅ |
| No AI-cloud traffic | `Get-NetTCPConnection` for llama-server/llama-swap PIDs → **zero** non-local established connections; only external host in the stack is duckduckgo.com (search, user-allowed), reached by the uvx MCP process | ✅ |

Satisfies TDD §7 Phase-4 exit criterion.

## Phase 5 — local FIM autocomplete (2026-06-12)

FIM model **qwen2.5-coder-3b-q8_0.gguf** (3.06GB) served by a dedicated `llama-server` on :8012 (`serving/start-fim.ps1`), independent of llama-swap. Editor: **llama.vscode 0.0.48** (VS Code), endpoint → `http://127.0.0.1:8012`.

| Metric | Result | Target | Status |
|---|---|---|---|
| `/infill` returns valid completion | prefix `def fibonacci(n): … return ` → `return fibonacci(n-1) + fibonacci(n-2)` | non-empty, correct | ✅ |
| FIM decode speed | **292 tok/s** | fast enough for inline | ✅ |
| FIM server alone (VRAM) | 6.6GB / 32.6GB | — | ✅ |
| **Coexistence: FIM + `coder-fast-lite` (64k) both resident** | **27.6GB / 32.6GB** | ≤32GB (fit) | ✅ ~5GB margin |

Resolves the Phase-1 "decide in phase 5" note: the **coder-fast-lite (64k ctx)** profile is the answer — it leaves room for the 3B FIM model alongside, so editor autocomplete and the agent run simultaneously. Full-128k `coder-fast` (30GB) cannot coexist with FIM; use `coder-fast-lite` when editing live.

The final "see completions appear inline in the editor" step is the user's to eyeball; the backend (server + extension + config + endpoint) is verified end-to-end via `/infill`.

**All phases (0–5) complete and verified — 2026-06-12.**
