# Deep research — model refresh: video / audio / LLM (2026-06-21, dive 3)

Third multi-agent dive (107 agents, 25 sources, 25 verified → 18 confirmed, 7 refuted). Completes the model angles the prior two dives left open. **Result: video (ANGLE 1) answered; audio + LLM (ANGLES 2/3) are persistently unverifiable via broad dives — they need *targeted* dives, not more breadth.**

## ANGLE 1 — Video: LTX-2.3 is the verified upgrade (for native audio+video)
**`Lightricks/LTX-2.3`** — a ~22B DiT that **natively generates synchronized audio + video in one joint forward pass** (asymmetric dual-stream: 14B video + 5B audio with cross-attention; ships `A2VidPipelineTwoStage` + `LipDubPipeline`). CONFIRMED at primary-source level (HF cards + GitHub + arXiv:2601.03233). This fills the exact gap the **silent** Wan2.2 TI2V-5B leaves.
- **Open weights** (946k downloads/mo) under the **LTX-2 Community License** — downloadable (not API-only), but **NOT permissive** (free under $10M revenue; model-as-a-service restricted). Not MIT/Apache.
- **32GB fit (gated-on-optional-install):** bf16 (46GB) **won't fit**; **`Lightricks/LTX-2.3-fp8`** (~23.5GB resident) fits; **`Lightricks/LTX-2.3-nvfp4`** (~21.7GB, the Blackwell/RTX-50xx path) fits with headroom. RTX 5090 operation confirmed (repo issue #27: "needs ~24GB, enable fp8, uninstall xformers"). Install frictions: CUDA >12.7, PyTorch ~2.7, Python ≥3.12.
- ⚠️ **The "beats Wan2.2 on quality + speed" claim is UNVERIFIED** — no primary head-to-head; only third-party blogs (zenn.dev etc.) claim ~10–14× faster I2V. The *native-audio differentiator is real + confirmed*; a quality/speed WIN over Wan2.2 for silent video needs a local benchmark before swapping the default.
- **Alternative:** `tencent/HunyuanVideo-1.5` (8.3B DiT, open weights, ~14GB offload floor, **video-only / no audio**) — comfortable 32GB fit but no audio upgrade; proprietary tencent-hunyuan-community license.

**Recommendation:** add **LTX-2.3 (nvfp4 on the 5090, or fp8)** as an *optional* video model specifically for **native audio+video** (the one thing Wan2.2 can't do) — tag *gated-on-optional-install*. Keep Wan2.2 TI2V-5B as the silent-video default until a local benchmark confirms LTX wins there too.

## ANGLE 2 — Audio: only the incumbent verified
`ACE-Step 1.5` confirmed: **MIT**, open weights (~10.1GB), fits 32GB easily (Tier-1 <4GB; full 4B-DiT+4B-LM needs ≥24GB — still fits). A strong, unbeaten incumbent. **Every challenger** (DiffRhythm2, YuE, VibeVoice, Higgs-Audio, Stable Audio successors, STT-beyond-Parakeet) produced **zero surviving claims** — sources fetched, none survived verification. Unverified.

## ANGLE 3 — LLM refresh: entirely unverified
**Zero claims survived.** Qwen3-Coder-Next(-80B-A3B), Qwen3-VL-32B GGUF quant sizes, gpt-oss updates, Huihui-abliterated Qwen3-Coder — all unconfirmed. The current Qwen3-Coder-30B / gpt-oss-120b / Qwen3-VL incumbents have no verified challenger here.

## Why ANGLES 2/3 keep failing — and the path forward
Two dives have now failed to verify the audio challengers + the LLM refresh: a broad dive's top-N verification cut favors well-documented incumbents and drops newer / sparser-sourced models. **Broad re-runs won't crack these.** If pursued, they need a **targeted dive** at exact repos for exact byte-sizes — the report's own steer: *"Qwen HF org pages, unsloth/bartowski GGUF repos for exact quant byte-sizes against a 32GB-minus-KV budget, the gpt-oss repo/changelog, Huihui-ai abliterated repos."* A precise, scoped follow-up — not another broad sweep.

## Net for DokiDex
- **Actionable now:** LTX-2.3 (fp8/nvfp4) as an optional native-A/V video model — *gated-on-optional-install*.
- **Confirmed-keep:** Wan2.2 TI2V-5B (silent-video default), ACE-Step 1.5 (music, MIT).
- **Open (need targeted dives or wait for maturity):** audio/voice challengers, the LLM refresh.
