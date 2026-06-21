# Deep research — FOCUS-3 media frontier (lip-sync · real-time canvas) (2026-06-21)

Multi-agent follow-up dive: **115 agents, 32 primary sources, 159 claims → 25 verified → 23 confirmed, 2 refuted.** Covers the gaps the first dive (`research-chat-media-roundtrip-2026-06.md`) left open. **Result: Angles 1 & 2 answered; Angles 3–5 (video / audio / LLM refresh) produced ZERO surviving claims and remain open.**

## Headline
- **Local lip-sync / talking-avatar is very feasible on 32GB** — the constraint is **license, not VRAM** (every option fits with room to spare).
- **Real-time sketch→image canvas** = the *original* StreamDiffusion + SD-Turbo (amply real-time on a 5090). StreamDiffusionV2 is a *different* tool (real-time video, Linux-only) — not the canvas.
- **The video/audio/LLM refresh still needs a dedicated verification pass** (candidates surfaced below but unverified).

## Angle 1 — Lip-sync / talking-avatar (all fit 32GB; pick by license)
The audio→video driver is consistently an **SD/MM-DiT diffusion model conditioned on Whisper or wav2vec2 audio features via cross-attention**. Picks:

| Model | VRAM | Driver | License | Verdict |
|---|---|---|---|---|
| **EchoMimicV3** (antgroup) | 12–16GB | Wan2.1-Fun-1.3B + wav2vec2 | **Apache-2.0 (clean, commercial-OK)** | **Best permissive pick** — the clean avatar add (AAAI-2026) |
| **MuseTalk** (TMElyralab) | ~4GB | non-diffusion latent-inpainting GAN + whisper-tiny | code MIT; **weights gated-unclear** | **Best real-time** (30fps+); single-step |
| **LatentSync 1.6** (ByteDance) | 18GB | SD-U-Net + Whisper + SyncNet | **NOT Apache-2.0** (refuted 0-3) — verify | Best raw quality; **license must be checked first** |
| **Sonic** (Tencent) | ~12–32GB | Stable Video Diffusion + whisper-tiny | CC BY-NC-SA 4.0 (non-commercial) | Feasible; personal-use-OK for DokiDex |
| **Hallo2** (Fudan) | feasible | wav2vec | core MIT; **4K path non-commercial** (S-Lab) | 4K + up-to-1hr; base ok, 4K gated |
| **HunyuanVideo-Avatar** (Tencent) | 24GB floor / 10GB via Wan2GP | MM-DiT + AEM + FAA | — | **Linux-tested**; Windows/low-VRAM via 3rd-party Wan2GP only |

**Recommendation for DokiDex:** add **EchoMimicV3** as the talking-avatar capability (clean Apache-2.0, 12–16GB, ComfyUI path) — *gated-on-optional-install*. Offer **MuseTalk** for fast/real-time lip-sync. Treat **LatentSync** as quality-tier *pending a license check*.

## Angle 2 — Real-time / latent canvas
- **StreamDiffusion (cumulo-autumn) + SD-Turbo** is the real **sketch→image canvas**: **106 fps txt2img / 94 fps img2img at 1 step on a 4090** → a 5090 meets-or-exceeds. LCM-LoRA + KohakuV2 @4 steps (38/37 fps) trades speed for base-model flexibility. ComfyUI/SwarmUI integration is practical. **Tag: feasible.** *(fps = Stream-Batch throughput at 512×512, not single-frame latency.)*
- **StreamDiffusionV2 ≠ the canvas:** it's a real-time **video** v2v system on Wan2.1; headline 58–64 fps needs **4× H100**, and it's **officially Linux-only** (no published single-GPU/5090 figure). **Tag: upstream-blocked for native Windows-11** (would need WSL2).

## Angles 3–5 — UNANSWERED (need a fresh verification pass)
Zero claims survived verification, so nothing is confirmed — but the dive *did* fetch primary sources naming these **candidates** (treat as leads, not verified):
- **Video:** `Lightricks/LTX-2` + **`Lightricks/LTX-2.3-fp8`**, Wan **2.5** (audio-sync; check open-weights status) vs Wan2.2 TI2V-5B.
- **Audio/music/voice:** **`ace-step/ACE-Step-1.5`**, **`ASLP-lab/DiffRhythm2`**, **`microsoft/VibeVoice`**, **`boson-ai/higgs-audio`** (vs ACE-Step + Chatterbox).
- **LLM:** **`Qwen/Qwen3-Coder-Next-GGUF`**, **`Qwen/Qwen3-VL-32B-Instruct-GGUF`**, **Huihui abliterated Qwen3-Coder-30B** (vs the current daily driver).

## Caveats
- **License gating is the dominant risk, not VRAM.** EchoMimicV3 is the only cleanly-commercial pick; Sonic/Hallo2-4K are non-commercial; LatentSync + MuseTalk-weights are unresolved. DokiDex is local/single-user/non-commercial, so the NC ones are usable — but none are commercial-safe by assumption.
- **Platform:** HunyuanVideo-Avatar + StreamDiffusionV2 are Linux-first (Windows via Wan2GP / WSL2 only).
- All figures are mid-2026 snapshots; re-verify at install. Full verified claim set + votes in the archived run output.
