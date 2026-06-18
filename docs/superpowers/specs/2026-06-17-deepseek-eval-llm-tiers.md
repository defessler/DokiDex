# DeepSeek evaluation → LLM speed/quality tiers, vision-gap fill, and best-setup verification

> **Status (2026-06-17): IMPLEMENTED.** The speed/quality tier selector, the vision-gap fill, and the
> bake-off scaffolding shipped on `feat/web-studio`; the bake-offs themselves and the optional rewriter swap
> are intentionally deferred (GPU-eval / optional). Verified by an adversarial multi-agent review (zero
> must-fixes). See `docs/decisions.md` (entry "LLM re-verification + speed/quality tiers + vision fill").
> This doc is the preserved analysis behind that work.

## Context

The owner asked whether DeepSeek is a good improvement over / addition to DokiDex's current local LLM stack and which workflows fit it; then relaxed the latency constraint ("fine even if it takes a few hours"); then added the real requirement — **a per-request speed/quality choice** ("keep the faster setup too… choose how fast I want a response"), plus a check that the current setup is the best quality that runs on this hardware.

Two adversarial multi-agent research passes (web + codebase, with skeptical verification) answered all three. Hardware was pinned directly: **RTX 5090 (32 GB VRAM), 64 GB DDR5-6400, i9-14900KS, Windows 11**; serving = **llama.cpp b9616 + llama-swap v224** on `:8080` (OpenAI-compatible), with the LLM group **GPU-exclusive** against the media group, and big-MoE CPU-offload already proven (gpt-oss-120b runs ~27 GB GPU + ~40 GB experts in RAM via `--n-cpu-moe 22`).

## Findings (the answer)

**DeepSeek — do not adopt.** Double-verified, including the "we already offload a 120B, why not a DeepSeek MoE?" angle and the relaxed-time angle:
- The governing fact is the **decode-speed law**: under `n-cpu-moe` offload, tok/s scales ~inversely with **active** params (you stream the active experts from RAM every token). gpt-oss-120b's 5.1 B active is exactly what buys its ~8–10 tok/s.
- **V4-Flash** (284 B / 13 B active) is the only DeepSeek with a gpt-oss-like profile and its 2-bit quality/tool-calls are reportedly usable — but 13 B active → **~3–5 tok/s** on this box, all quants exceed 32 GB (heavy offload mandatory), and it rides an **unmerged experimental llama.cpp fork** (PR #22378, "no intention to merge") that collides with the pinned b9616 + the eval-gate discipline. The fork status is a hard blocker independent of the relaxed time budget.
- **671 B flagships (V3/V3.1/V3.2/R1, 37 B active)**: <2 tok/s under offload (datacenter-only). Even with "hours OK," R1-class models emit ~20 K+ reasoning tokens → **hours per single answer** + fragile (one crash wastes the run). V3.2's Sparse Attention cuts long-context KV, **not** the active-expert bottleneck.
- **Distills**: R1-Distill-Llama-70B at 2-bit fits/fast but degraded + weak agentic tool-calls (the exact disqualifier class the repo already rejected); V2-Lite/Coder-V2-Lite fit/fast but are 2024-era quality, below the incumbents. Censorship is also baked into the weights (inherited by distills); uncensored finetunes exist but are moot given the above.
- **Re-evaluate only if** V4-Flash merges into upstream stable llama.cpp **and** someone posts a measured single-32 GB + DDR5 `n-cpu-moe` run ≥ 8 tok/s with clean tool-calls.

**The setup is mostly best-for-hardware — but the "no 32 GB upgrade exists" conclusion is now stale in two slots, and one slot is an unfilled hole.** The field moved since the last refresh, and b9616 (2026-06-15) already supports the new architectures (qwen35moe merged 2026-04-19; GLM MoE; Qwen3-VL), so no llama.cpp upgrade is needed to try them.
- **Heavy** (gpt-oss-120b) — confirmed best (only proven 100B-class hybrid-offload fit on 32 GB; GLM-5.x is cloud-only, GLM-4.5-Air offloads slower at 12 B active).
- **FIM** (Qwen2.5-Coder-3B) — confirmed best (no better *small* infill model in 2026; deliberately 3 B for latency + coexistence).
- **Rewriter** (Qwen2.5-3B) — effectively optimal; the tuned MagicPrompt instruction is the load-bearing part. Qwen3-4B-Instruct-2507 is a marginal optional upgrade.
- **Coder** — the repo's own re-judge trigger has **fired**; the "Qwen3.6-35B-A3B doesn't exist (invented)" note in `decisions.md` is now stale. Credible untested challengers exist: **Qwen3.6-27B (dense)**, **Qwen3.6-35B-A3B**, **Qwen3-Coder-Next-80B-A3B**.
- **Vision** — genuine hole: `Vision.cs` Describe + Verify are built but dark (no vision model installed).

## Plan (priority order)

### 1. Per-request speed/quality tier selector (the explicit requirement) — DONE
Expose "how fast vs how good" on the **latency-tolerant, one-shot** LLM workflows only — **Director** (script→shotlist), **PitchDeck** (logline/synopsis), **MultiCharacter** relationship phrasing. **Exclude the rewriter and FIM** — they must stay fast (a slow/reasoning model there ruins TTFT).
- **Default = Fast.** The user opts into **Quality** per request.
- **Mechanism:** llama-swap routes by model *name*, so a tier = a model name. `LlmTiers.Resolve(tier)` → `coder-fast` (Fast) / `coder-big` (Quality) / null (unknown → omit the OpenAI `model` field → llama-swap's loaded default = pre-tier behavior). `LocalLlm.ChatAsync`/`ChatVisionAsync` gained an optional `model`; `LocalLlm.Body` sends `model` only when non-null. Director/PitchDeck thread the tier; the `/compose/multichar` endpoint optionally routes the relationship phrase through the LLM at the chosen tier (default literal/pure; LLM-down → literal). SPA Speed selectors on Director, the story-bible export, and Cast.

### 2. Fill the vision gap (highest value, lowest risk, zero studio code change) — DONE
A gated `vision` block in `serving/llama-swap.yaml` + `setup.ps1 -Vision` download lights up the already-built Describe/Verify surfaces (`control/Web/Vision.cs`) over the existing `:8080` `image_url` path; `Vision.cs` targets `LlmTiers.Vision`.
- **Primary:** `unsloth/Qwen3-VL-8B-Instruct` GGUF + `mmproj`, full GPU, **Instruct (not Thinking)** — CoT pollutes the single-line Describe and the PASS/FAIL `ParseVerdict`.
- **Uncensored fallback** on the same path: an abliterated Qwen2.5-VL-7B caption GGUF / JoyCaption.
- **Gate:** confirm b9616 has Qwen3-VL CLIP support by loading one real image; then a Describe + Verify smoke incl. NSFW-adjacent. The vision model is in the LLM group (evicted while media generates — added swap latency by design).

### 3. Coder + heavy bake-offs (measure-then-decide) — SCAFFOLDED, deferred to GPU eval
Commented candidate blocks in `serving/llama-swap.yaml` + `setup.ps1 -LlmCandidates` downloads; **do not blind-swap** (incumbents won on tool-call reliability; Nemotron 45 %, REAP-48B refused tools).
- **Coder:** `Qwen3.6-27B` (dense, lead) and `Qwen3.6-35B-A3B` (alt), text-only / `think:false`.
- **Heavy:** the **full** `Qwen3-Coder-Next-80B-A3B` (not the rejected REAP-48B prune), CPU-offload.
- **Gate:** `serving/test-toolcall.ps1` + `evals/run-suite.ps1` — adopt only on **≥ 91 % golden AND zero tool-call flakes**; the winner becomes that tier's model.

### 4. Optional / no-action
- **Rewriter:** optional swap to `Qwen3-4B-Instruct-2507` — **not taken** (the tuned instruction is load-bearing; marginal gain).
- **FIM:** keep Qwen2.5-Coder-3B Q8. **DeepSeek:** no action (re-evaluate only under the conditions above).

## Verification
- **Tier selector:** a Director call on Fast vs Quality routes to the expected model; fast-tier TTFT unchanged; rewriter/FIM untouched. (`LlmTiers.Resolve` is unit-tested; review confirmed end-to-end wiring + that null tier preserves pre-tier behavior.)
- **Vision:** one real image loads under b9616; Describe returns a prompt, Verify returns PASS/FAIL; NSFW-adjacent on the uncensored fallback.
- **Bake-offs:** `evals/run-suite.ps1` ≥ 91 % and `serving/test-toolcall.ps1` zero flakes **before** any swap.
- **Build:** `dotnet build … --no-incremental` + `dotnet test` (suite green at 354/0 post-change); standalone web exe builds.
