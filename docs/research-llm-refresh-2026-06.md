# Deep research — LLM refresh (targeted) (2026-06-21, dive 4)

Targeted multi-agent dive (104 agents, 22 sources, 25 verified → 23 confirmed, 2 refuted) — the focused follow-up that **cracked the LLM angle two broad dives couldn't** (exact-repo / exact-byte-size targeting works where breadth failed). Budget that matters: a candidate must fit **quant + q8 KV + ≥32k context fully on the 32GB 5090**.

## Two adopt-ready upgrades (feasible, fit 32GB, llama.cpp-supported)
- **Vision tier → Qwen3-VL-32B-Instruct** (`Qwen/Qwen3-VL-32B-Instruct-GGUF`, or `unsloth/…UD-Q4_K_XL`) — upgrades the current Qwen3-VL-8B. **Q4_K_M = 18.40 GiB** (UD-Q4_K_XL ~18.7 GiB) + an explicit **mmproj** projector (F16 1.11 GiB / Q8_0 0.72 GiB) + q8 KV + ≥32k ctx fits 32GB. Vision is **upstream in llama.cpp** — PR #16780 (merged 2025-10-30): mmproj/CLIP + DeepStack + Interleaved-MRoPE, dense+MoE, CUDA (build b6887+). Load `--mmproj` explicitly (the bare `-hf` serves text only). ⚠️ Q8_0 (32.43 GiB) does NOT fit; known post-merge bug #17200 (KV-cache fault on a 2nd consecutive multimodal request). **Tag: feasible.**
- **Fast fully-on-GPU tier → gpt-oss-20b** (`ggml-org/gpt-oss-20b-GGUF`, MXFP4, by ggerganov) — **11.28 GiB**; total **14.9 GB @8k / 17.9 GB @131k** → fits with ~14 GB headroom, **no CPU-offload** (unlike the gpt-oss-120b heavy tier), native llama.cpp/llama-swap. ⚠️ q8 KV reportedly halves gpt-oss quality — prefer **F16 KV** (still fits). **Tag: feasible.**

## Real but over-budget (offload-only)
- **Qwen3-Coder-Next** (`Qwen/Qwen3-Coder-Next`) — REAL (created 2026-01-30, ~1.1M downloads), the **80B-A3B hybrid MoE** (Qwen3-Next: Gated DeltaNet 3:1 Gated Attention, 512 experts). llama.cpp supports it — PR #19324 (Feb-4, fixes looping) + a Feb-19 tool-call parse fix → **pin a recent build + re-download the GGUF**. BUT the smallest practical 4-bit is **~36 GB+** (Unsloth: 46 GB for 4-bit, 85 GB for 8-bit) — exceeds 32GB for weights alone → **needs CPU-offload like gpt-oss-120b** (~38–48 tok/s streaming from RAM). **Tag: upstream-feasible but over-budget** — a candidate *heavy* tier (offloaded), NOT a fully-on-GPU daily-driver swap. (Only Q2_K ~27GB fits fully, quality-degraded.)

## Open gap
- **Abliterated / uncensored Qwen3-Coder** (`mradermacher/Huihui-Qwen3-Coder-30B-A3B-Instruct-abliterated-i1-GGUF`) — UNVERIFIED (zero surviving claims). By analogy to the ~17.7GB incumbent, a same-arch abliterated Q4 would *plausibly* fit 32GB, but abliteration can degrade tool-calling — needs a dedicated file-tree + card pass.

## Net for DokiDex
- **Adopt-ready (your call → download + GPU-test):** **Qwen3-VL-32B** (vision-tier upgrade over the 8B) · **gpt-oss-20b** (fast fully-on-GPU tier; the 120b needs offload).
- **Consider:** Qwen3-Coder-Next 80B-A3B as an offloaded *heavy* tier (vs/alongside gpt-oss-120b) if ~40 tok/s is acceptable.
- **Daily driver unchanged:** Qwen3-Coder-30B-A3B stays the best *fully-on-GPU* coder (Next is too big to fit).
- **Still open:** the abliterated coder (one more targeted file-tree pass), and the audio/voice challengers (DiffRhythm2 / YuE / VibeVoice / Higgs — a separate targeted dive).
