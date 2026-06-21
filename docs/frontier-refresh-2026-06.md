# DokiDex Frontier-Refresh Roadmap (2026-06-21)

_Multi-agent ultracode research, 46 agents, primary-source + adversarial verification._

**Headline:** DokiDex is feature-richer than its own brief admits — the synced-A/V keystone is now partially unblockable (LTX-2.3-fp8 in SwarmUI, lip-sync via the already-shipped ComfyUI sidecars), the single highest-leverage build is finishing the generate-from-chat GPU handoff, and the GLM-4.7-Flash bake-off is the one model A/B worth running now.

---

## DokiDex — Prioritized "What's Next" Roadmap (2026-06-21)

**Headline:** The synced-A/V keystone is *now partially unblockable* (LTX-2.3-fp8 loads natively in SwarmUI; true lip-sync runs through DokiDex's already-shipped ComfyUI sidecars — not a SwarmUI class). The single highest-leverage build is **finishing the generate-from-chat GPU handoff** (queue is shipped; the render round-trip is not). The one model bake-off worth running now is **GLM-4.7-Flash vs Qwen3-Coder-30B**, but a verification caveat changes the setup the research proposed — read R2 carefully.

---

### Ground rules applied
- **Eval-gate discipline honored:** no default swaps proposed; every model move below is gated on a measured blind A/B (`serving/test-toolcall.ps1` + `evals/run-suite.ps1`, ≥91% golden AND zero tool-call flakes). Incumbents hold until a measured win.
- **Confirmed-only:** claims marked `confirmed` in adversarial verification are treated as established. Claims marked `refuted` or `unverifiable` are explicitly flagged as **NOT established** and I do not build on them.
- **Hard constraint:** one 32GB RTX 5090, Windows 11, no Docker, GPU mutually-exclusive between the LLM group and the media group. Every item respects this.
- **Repo facts I re-verified directly:** `serving/llama-swap.yaml` is on build b9616; the commented `coder-candidate-glm` (line 116) sets `--flash-attn on` with KV-quant correctly OFF; the 2026-06-13 candidate scored **5/11 (45%)** vs the incumbent's **10/11 (91%)** — both scorecards present on disk.

---

## Is the lip-sync / synced-A/V keystone unblockable? YES — partially, and not the way the old roadmap guessed.

Three confirmed findings change the picture:

1. **LTX-2.3 (22B, one-pass synced A/V) is now loadable in SwarmUI — via FP8 safetensors, NOT the unsloth GGUF.** `confirmed`: SwarmUI has a native class `lightricks-ltx-video-2-3` (`isLtxv23`, detected via `text_embedding_projection.audio_aggregate_embed.weight`), actively maintained through 2026-03 commits; Lightricks officially hosts `Lightricks/LTX-2.3-fp8` (~29GB dev / ~29.5GB distilled) and SwarmUI's doc points to it as the install. The old "no loadable checkpoint" blocker is **resolved**.
2. **The roadmap's speculative fix ("unsloth GGUF in SwarmUI") is NOT established.** The claim "GGUF of LTX-2.3 doesn't work in SwarmUI because ComfyUI lacks support" was **refuted** — ComfyUI *does* support LTX-2.3 GGUF (city96 PR #399, merged 2026-01-11). But whether SwarmUI's own loader runs an LTX-2.3 GGUF end-to-end is **unverifiable** from primary sources. **Net: use FP8 in SwarmUI as the safe path; the GGUF-in-SwarmUI route is unproven, not dead — re-recon it on-box, don't write it off.**
3. **True audio-driven lip-sync is NOT a SwarmUI feature — and DokiDex already shipped the right answer.** `confirmed`: SwarmUI exposes no audio-input lip-sync (its native audio is ACE-Step only); Wan2.2-S2V still has no native SwarmUI class (re-confirmed in current `T2IModelClassSorter.cs`). Also `confirmed`: DokiDex already install-wired **LatentSync** (the light pick, ~8GB at 1.5, Apache-2.0 code / OpenRAIL++ weights) and **InfiniteTalk** as ComfyUI sidecars, with the workflow JSON deliberately deferred to on-GPU authoring.

**How to close it:** finish the LatentSync ComfyUI workflow JSON on the GPU (the one remaining, explicitly-deferred step), and separately eval-gate LTX-2.3-fp8 in SwarmUI as a Tier-2 synced-A/V *clip* capability (NOT a default; 32GB is the hard floor — stay ≤720p/≤4s, prefer the distilled variant). On a 32GB box at FP8, official render-time reports show 720p/1080p only and no native 4K — treat "4K one-pass" as marketing.

---

## Which model bake-offs are worth running now?

- **GLM-4.7-Flash (30B-A3B, MIT)** — RUN IT. Strongest true-32GB in-class challenger, pure-GPU (~17.5GB UD-Q4_K_XL), `deepseek2`/MLA runtime confirmed, and b9616 already contains the needed PRs (`confirmed` via git ancestry). See R2 for the flag caveat.
- **Qwen3-Coder-Next-80B-A3B (Apache-2.0)** — OPTIONAL, vs `coder-big` (gpt-oss-120b) only. RAM-offload class (Q4 ~45–48GB, not pure-GPU); lower tool-call risk than the 3.6 line; b9616 has the `key_gdiff` fix. Lower priority than GLM.
- **Qwen3.6-27B / 35B-A3B** — DEFER (blocked-on-tooling). `confirmed`: an OPEN llama.cpp bug (#22684, fix PR #24202 still open) routes their tool calls into `reasoning_content` with `tool_calls` absent — a textbook flake against an OpenAI-compatible harness. DokiDex's own prior bake-off already measured 27B at 45%. Re-test only after a llama.cpp bump that closes #22684.
- **NOT worth touching (out of hardware budget):** GLM-5.2 (753B), MiniMax-M3, Kimi-K2.6 — `confirmed` they cannot run on one 32GB GPU even with 64GB RAM offload.

---

## What is the single highest-leverage build?

**Finish the generate-from-chat GPU handoff (P1 below).** It's mostly wiring over shipped parts (the durable queue, the evict→switch→wait GPU guard, and the conversation back-link all already exist — `confirmed`), it puts DokiDex's strongest asset (media) inside its chat surface, and no other product solves this for a single mutually-exclusive GPU. That arbitration *is* the differentiator.

---

## Ranked next moves

> Tags: **feasibility** = feasible-now / gated-on-eval-A-B / gated-on-optional-install / upstream-blocked · **effort** = S/M/L · **impact** = the payoff.

### P1 — Finish generate-from-chat: the chat→media render round-trip
**feasible-now · M · Impact: HIGH** — Completes the brief's named deferred item using DokiDex's best asset. `confirmed`: the queue (`PendingGenStore` → `/api/pending-gen` Create-view chips) and the GPU arbitration machinery (`renderGuard()`/`ensureMedia()`/`waitForMedia()`, plus `PendingGen.Conversation` back-link) are shipped; only the user-confirmed switch → drain → return-inline bubble + restore-to-agent UX is missing. Make the ping-pong explicit (it's destructive to the resident chat model mid-session). Net-new = a drain endpoint + an inline-result bubble.

### P2 — Persistent long-term chat memory (the #1 real gap)
**feasible-now · M · Impact: HIGH** — `confirmed` DokiDex has no cross-conversation memory today (recall is keyword-only lorebook + per-KB doc RAG). Build the LibreChat-style **editable key/value "memory agent"** first: a file-based `MemoryStore` mirroring `KbStore`/`ChatStore`, one extra `coder-fast` call per turn injecting a bounded `[Memory]` block via the existing `ChatPrompt.Build` seam. Zero new VRAM, near-pure clone-work. Then layer the **semantic complement** (SillyTavern-style auto-extract + vector recall) over the *already-shipped* `:8090` embed server + `DocSearch` pointed at a memory scope — but apply it as bounded recall, not every token (it breaks prompt-caching). Patterns are `confirmed`; the effort-mapping is sound inference grounded in verified seams.

### P3 — Finish the LatentSync lip-sync workflow on-GPU
**gated-on-optional-install · S–M · Impact: HIGH (closes a frontier gap)** — The install-wiring is `confirmed` shipped; the only remaining step is authoring + verifying the LatentSync ComfyUI workflow JSON on the GPU. ~8GB VRAM, fits with huge headroom alongside the media group. This is the realistic "talking head" answer — do NOT wait for a SwarmUI S2V class (`confirmed` it doesn't exist).

### P4 — GLM-4.7-Flash bake-off vs coder-fast
**gated-on-eval-A-B · S (setup) + M (eval) · Impact: HIGH if it wins** — Strongest in-class challenger; pure-GPU; b9616 has the MLA PRs (`confirmed`). **Verification caveat that changes the research's proposed fix:** the research recommended flipping `--flash-attn on`→`off`, but the supporting claim ("GLM flash-attn broken on Blackwell sm_120, issue #19307") was **REFUTED** in adversarial verification — that assert was an AMD ROCm/RDNA2 bug, the RTX 5080 in #19307 was *working*, #18944 was already FIXED on CUDA by PR #18953 (which *added* FA), and the real CUDA quant-KV crash is on Ampere (#19036), not Blackwell. **So treat the flag question as a per-box empirical variable, not a settled fix:** run the A/B first with the current `--flash-attn on` + KV-quant OFF (the KV-quant-OFF note is independently `confirmed` correct); only disable FA if *this 5090* actually faults. Note bartowski's card suggests `--flash-attn off` for *performance*, not crash-avoidance — that's a tuning knob, decide it by measurement. Gate on ≥91% golden + zero tool-call flakes vs the 91% incumbent scorecard.

### P5 — Artifacts / canvas panel (render Mermaid / HTML / SVG from a chat turn)
**feasible-now (render) · M · Impact: MEDIUM-HIGH** — `confirmed` DokiDex renders zero artifacts today (chat is `textContent`-only by design). Detect fenced `html`/`mermaid`/`svg` and render in a sandboxed iframe beside the transcript — front-end-only over the existing single-file SPA, 0 VRAM. **Correction to the brief's framing:** the "Open WebUI 0.8.x / 2026" provenance was **refuted** (these shipped in OWUI 0.2.x–0.5.x, 2024–2025) — but the directional gap (rivals render artifacts, DokiDex doesn't) stands. A sandboxed code-interpreter is a *later, gated* sub-step; keep it off the curated 4-tool default per DokiDex's own tool-sprawl discipline.

### P6 — RAG reranking for KB + memory retrieval
**gated-on-optional-install · S–M · Impact: MEDIUM** — `confirmed`: llama.cpp has a native `/v1/rerank` endpoint and the official `ggml-org/Qwen3-Reranker-0.6B-Q8_0-GGUF` (639MB, Apache-2.0) is the working conversion. Retrieve N → rerank to K sharpens both KB and the P2 memory recall. **Two confirmed constraints:** (a) it needs a **second small llama-server process/port** (the embed `:8090` can't also rerank — one model + one pooling-type per process; note the research's stated reason "flags are mutually exclusive" was *refuted*, but the second-process conclusion holds for the right reason); (b) pin the **official** ggml-org GGUF and verify scores are non-degenerate (community GGUFs are broken; open correctness bug #16407). `unverifiable` whether scoring is perfect out-of-the-box — verify before trusting.

### P7 — Eval-gate LTX-2.3-fp8 in SwarmUI as a Tier-2 synced-A/V clip capability
**gated-on-eval-A-B · M · Impact: MEDIUM (new capability, not a default)** — Loadable today via FP8 (`confirmed`). Keep Wan 2.2 the video default; add LTX-2.3 only as an eval-gated synced-A/V *clip* tool. On-GPU, **verify SwarmUI actually emits the synced audio track** (its audio doc is silent — `unverifiable` from the desk) and stay ≤720p/≤4s. Distilled variant is the realistic 32GB speed arm.

### P8 — HunyuanVideo 1.5 blind A/B vs Wan 2.2 (video)
**gated-on-eval-A-B · M · Impact: MEDIUM** — `confirmed`: Tencent 8.3B, SwarmUI- + ComfyUI-native, fits 32GB with large headroom (~14GB w/ offload). **Corrected expectation:** the win is *not* the clean split the brief implied. Per Tencent's *own* human eval (`refuted` the "Wan wins human-subject fidelity" framing): Hunyuan beats Wan overall (+17% T2V / +12.6% I2V GSB) and on instruction-following/motion by large margins; structural stability is mixed (Hunyuan wins T2V, Wan wins I2V); there is **no** photorealism/face/skin dimension in any primary source, and the third-party "8.4 vs 7.6" figure appears fabricated. Run it as a genuine blind A/B; don't assume a photoreal-vs-motion trade. SwarmUI auto-detect is `confirmed` for the base class but partial for SR/I2V-V2 subvariants (may need manual metadata).

### P9 — Adopt OpenCode sub-agents (plan = coder-big, execute = coder-fast)
**feasible-now · S · Impact: MEDIUM (orchestration lever, no model re-gate)** — `confirmed`: OpenCode ships primary/sub-agents with per-agent `model` overrides against a local OpenAI-compatible endpoint, and DokiDex's `harness/opencode.json` already targets `:8080`. **Honest constraint:** GPU exclusivity means this is *sequential* plan→execute via llama-swap, not concurrent — frame it that way. Pair with **Serena** (MIT, LSP-over-MCP, fully local, `confirmed` v1.5.3) for precise symbol-level code intelligence to complement the existing cosine `code_index`. No model risk, no eval-gate to adopt the harness feature.

### P10 — Lower-leverage surfacing (batch when convenient)
**feasible-now · S each · Impact: LOW-MEDIUM** — All confirmed as local-feasible, none a capability gap: **turn-based voice/call mode** (Parakeet STT + llama-swap + Chatterbox TTS all exist; net-new = streaming loop + barge-in — but snappy barge-in on a busy single GPU is the `unverifiable` part); **multi-model split-chat / verify panel** (Msty pattern, thin front-end over llama-swap tiers); **text group-chat turn-taking** (`MultiCharacter.cs` is image-only today; group chat = N sequential persona calls).

### Explicitly DEPRIORITIZED
- **Optional Qwen3-Coder-Next-80B vs gpt-oss-120b** — `gated-on-eval-A-B · M`. Worth it only if you want to revisit `coder-big`; RAM-offload class, tune to ~30GB VRAM via `--n-cpu-moe`.
- **Computer-use / UI-TARS** — `upstream-blocked` (as a chat fold-in). `confirmed`: needs a dedicated action-VL model that contends for the single GPU and ships as a separate app stack. Doesn't compose with DokiDex's one-group-at-a-time arbitration. Future optional mode, not now.
- **Model NON-adopts (off the table, `confirmed`):** Qwen-Image-2.0 (API-only, no weights), Wan 2.5/2.6/2.7 (proprietary), FLUX.2 Dev (VRAM-tight + non-commercial), Stable Audio 3.0 (instrumental-only — does NOT replace ACE-Step's vocal songs). Image/edit incumbents (Z-Image, Qwen-Image-Edit-2511) and music (ACE-Step 1.5) HOLD — no open challenger displaces them this window.

---

### What the brief got wrong (so you don't rebuild shipped features)
`confirmed` ALREADY SHIPPED, not gaps: in-chat **web search** (ddgs sidecar), generate-from-chat as a **durable queue**, named cross-conversation **Knowledge Stacks** with doc ingest + OCR, persona cards + lorebooks, vision-in-chat, the bounded 4-hop tool loop, voice readback, and chat branch/fork/regenerate. The genuine gaps are memory (P2), the render round-trip (P1), artifacts (P5), and reranking (P6).

### Repo touch-points (for the implementer)
- P1: `control/Web/PendingGenStore.cs`, `control/Web/ChatTools.cs` (RunGenerateImage), `control/Web/wwwroot/index.html` (~1428 loadPendingGen, ensureMedia/renderGuard)
- P2: clone `KbStore.cs`/`ChatStore.cs`; inject via `ChatPrompt.Build`; reuse `DocSearch.cs` + `:8090`
- P3: LatentSync workflow JSON (ComfyUI sidecar, install-wired per `docs/decisions.md`)
- P4: `serving/llama-swap.yaml` line 108–116 (uncomment `coder-candidate-glm`), `serving/test-toolcall.ps1`, `evals/run-suite.ps1`
