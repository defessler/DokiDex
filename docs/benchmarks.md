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

## Phase 6 — media + speech + memory (2026-06-14)

Same RTX 5090 / 32 GB box, all fully local. Media times are **per gen including the model
load/swap** — the realistic cost when switching capabilities (the 32 GB card runs one model at a
time, so each switch reloads). The *first* gen after `doki up media` also pays SwarmUI's ~30 s
cold-start; steady-state (model resident) is faster — note how the warmer consecutive gens drop.

### Speech + memory (agent mode — coexists with the coder)

| Capability | Measurement | Notes |
|---|---|---|
| **TTS** — Chatterbox `:8004` | **4.4 s** for a 9-word sentence | ~4 GB VRAM; rides alongside coder-fast |
| **STT** — Parakeet (onnx-asr) `:8005` | **5.8 s** to transcribe a ~3 s clip | CPU EP (no VRAM) |
| **Memory search** — sqlite FTS5 | **< 5 ms** | + python startup if shelled |

### Image / video / audio (media mode)

| Capability | Settings | Time (incl. load) |
|---|---|---|
| **Image** — Z-Image Turbo | 1024×1024, 8 steps | 48.3 s¹ |
| **Video** — Wan 2.2 TI2V-5B | 832×480, 49 frames, 20 steps | 26.0 s |
| **Image-to-video** — Wan 2.2 5B | 832×480, 25 frames (warm 5B) | 20.7 s |
| **Fast video** — LTXV-2b distilled | 768×512, 97 frames, 8 steps | **10.6 s** |
| **Music** — ACE-Step 1.5 turbo | 10 s instrumental | 12.0 s |
| **Upscale** — 4×-UltraSharp | 512 → 1024 (incl. base gen) | 11.8 s |
| **Image-edit** — Qwen-Image-Edit-2511 | 512×512, 20 steps | 41.5 s² |

¹ first gen after `up media` — includes SwarmUI's ~30 s cold-start + the Z-Image load; steady-state
Z-Image Turbo is ~3–5 s. ² includes the ~20 GB Qwen model load.

**Takeaways:** LTXV is the speed champ (97 frames in ~10 s — near-real-time class). Wan 2.2 5B is
~26 s for a ~2 s 480p clip at full quality. The 32 GB single-model constraint means switching
*capabilities* costs a model swap; consecutive gens within one model are much faster. All
uncensored, all local. (FIM autocomplete is measured under coexist mode — see Phase 5.)
