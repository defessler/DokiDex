# AI-platform research → models + workflows to fold into DokiDex (2026-06-18)

> **Method:** three `deep-research` harness passes (fan-out web search → fetch → **3-vote adversarial
> verification, 2/3 to kill a claim** → synthesize). ~**335 subagents, ~8.1M tokens, 46 claims confirmed /
> 8 refuted** against primary sources (official GitHub repos, HF model cards, SwarmUI/ComfyUI docs, the
> platforms' own pages). Goal: find which AI sites use which models/workflows, and fold the best into
> DokiDex's local 32 GB RTX 5090 / SwarmUI + llama.cpp stack. Relates to [[media-web-app-direction]] and
> [[prefer-self-contained-onbrand-installer]].

**Tags:** ✅ feasible now · 🔶 gated on optional install · 🧪 fits-but-hardware-verify · 📋 license-gated · ⛔ upstream-blocked

---

## 0. Headline

The research **validates almost all of DokiDex's existing generation surface** (Z-Image, the camera rig,
FLF2V, effect presets, the real-time scratchpad, stem separation all map to what the top platforms do), so
the real opportunities are narrow and high-value:

1. **The chat/assistant surface** — DokiDex's one true product gap. The backend already exists; full design
   in the sibling doc **`2026-06-18-chat-assistant-surface-design.md`** (persona-first spine, approval-gated).
2. A **free in-family music upgrade** (ACE-Step → 1.5).
3. A **15-engine voice node** (TTS-Audio-Suite) that collapses every TTS integration into one install.
4. **Specialist primitives DokiDex lacks**: face-identity (PuLID-Flux), lip-sync (InfiniteTalk), anime
   (Illustrious / Animagine), in-image-text (Qwen-Image-Edit), and a 14B-GGUF quality video tier (Wan 2.2 A14B).

Most consumer "websites" (Krea, NovelAI V4/V4.5, Recraft, MiniMax/Hailuo, Runway/Pika/Kling/Veo) run
**proprietary or aggregated hosted models** — so the portable value is the **UX patterns + the open
checkpoints SwarmUI already supports**, not chasing closed APIs.

---

## 1. Models to adopt or swap on 32 GB (consolidated, all 3 passes)

| Use-case | Incumbent | Verified move | Why / evidence | Tag |
|---|---|---|---|---|
| **Photoreal image** | Z-Image Base/Turbo | **Keep** | SwarmUI's own Jan-2026 doc ranks Z-Image **#1, "best for photoreal."** Confirms the `decisions.md` call. | ✅ |
| **Real-time canvas** | Z-Image-Turbo (scratchpad) | **Keep — it's the right base** | Z-Image-Turbo = 6B distilled, **8 NFEs, sub-second on H800, fits 16 GB, native ComfyUI**. Use FP8/GGUF for lowest live-loop latency. | ✅ |
| **Art-style / editing image** | Qwen-Image-Edit | **+ FLUX.2 Klein 4B/9B** | SwarmUI-native; the realistic on-32 GB FLUX.2 path for art-style variety + editing. | 🔶 |
| **Quality ceiling image** | — | FLUX.2 Dev 32B | "Smartest," but BF16 ≈64 GB → needs **GGUF Q4/Q5** to fit. | 🔶 (heavy) |
| **In-image text (edit)** | — | **Qwen-Image-Edit** | **Native ComfyUI** (fp8 ~20 GB); bilingual **add/delete/modify** text preserving size/font/style. Most turnkey. | ✅ |
| **In-image text (gen)** | — | Qwen-Image 20B (GGUF) | Q4_K_M 13.1 GB / Q5_K_M 14.9 GB; leading open text-rendering base. | 🔶 (city96 ComfyUI-GGUF node) |
| **Anime / illustration** | (generalist) | **Illustrious-XL v1.0** &/or **Animagine XL 4.0** | Both SDXL-family (OpenRAIL++), run trivially on 32 GB; Illustrious is native **1536 px** + Danbooru-tag. ⚠️ **Pony V7 is NOT SDXL** (7B AuraFlow DiT) — different loader/VRAM. | 🔶 |
| **Face / identity** | — | **PuLID-Flux** (prefer `sipie800/…-Enhanced`) | Zero-shot face-ID on FLUX.1-dev, ~22 GB bf16 / ~12 GB fp8-gguf — fits 32 GB; ComfyUI node; original is Alpha V0.1.0. | 🔶 |
| **Video (quality)** | Wan 2.2 TI2V-5B | **+ Wan 2.2 A14B GGUF (paired high/low-noise)** | SwarmUI: *"Wan 2.2/2.1 in 14B… the best you can get locally."* Q4_K_M ~9.6 GB each; **measured 832×480/81f in ~125 s on a 5090**. Keep 5B as the simple single-model default. | 🔶 |
| **Video (draft / real-time)** | — | **LTX-Video / LTX-2.3** | SwarmUI: *"wildly fast… at the cost of quality."* 720p–1080p, ~5 s (121f), **not** native 4K; ComfyUI template confirmed. | 🔶 |
| **Video (sync audio)** | — | LTX-2 19B | Unique **synchronized audio+video in one model**; nvfp4 ~20 GB + first-party ComfyUI — but vendor says **"32 GB min / 80 GB rec,"** so it's at the ceiling. Eval only. | 🧪 |
| **Lip-sync / talking-head** | — | **InfiniteTalk** | Audio-driven V2V dubbing + I2V, Apache-2.0, ComfyUI node, on Wan2.1-I2V-14B base. | 🔶 🧪 |
| **Music** | ACE-Step | **→ ACE-Step 1.5** | In-family upgrade: 600 s tracks, 50+ langs, batch-8, **stems + repaint + cover + vocal-to-BGM + LoRA**; turbo <4 GB. | ✅ (cover/repaint nodes "coming soon" in ComfyUI) |
| **Voice / TTS** | Chatterbox | **Keep Chatterbox via TTS-Audio-Suite; + IndexTTS-2, Higgs v3** | Chatterbox confirmed best-in-class. **TTS-Audio-Suite = 15 engines + RVC voice-conversion in ONE node.** IndexTTS-2 adds **duration + emotion/timbre disentanglement** Chatterbox lacks; Higgs v3 adds **100+ langs + inline `<\|sfx\|>` tags**. | 🔶 / 📋 (Higgs research-license) |
| **Speed lever (all diffusion)** | fp8 | **Nunchaku NVFP4 + GGUF/MultiGPU offload** | NVFP4 ~3× over BF16 on Blackwell; ComfyUI-GGUF + MultiGPU/DisTorch = the Forge "GPU-weight slider" equivalent. (The famous Nunchaku "4.4 s" is 4090/int4 — does NOT transfer.) | 🔶 |

**Music alternative (note, not adopt):** DiffRhythm2 — Apache-2.0 code **and** weights, but **no ComfyUI node**
(v1 only). ACE-Step 1.5 wins on ComfyUI-readiness.

**License flags:** Ideogram-4 open weights (strong in-image text, nf4 fits ≤24 GB) and Higgs Audio v3 are
**non-commercial / research-leaning** — fine for DokiDex's single-user local use, blocking for redistribution.

---

## 2. Workflows / UX patterns

### 2.1 The chat/assistant surface — the gap (full design in the sibling doc)
All three reference apps (**Open WebUI, LM Studio, SillyTavern**) are *pure frontends over an
OpenAI-compatible `/v1`* — exactly what DokiDex's llama-swap endpoint already is. So it's a frontend build,
not an inference project. Proven pieces to lift:
- **Assistant contract** `:8080` already speaks: streaming · tool/function-calling · vision · structured JSON.
- **Open WebUI = RAG + agentic reference:** hybrid BM25+vector + rerank, swappable vector DB, **MCP Streamable
  HTTP + OpenAPI auto-discovery** (DokiDex already runs a code-RAG MCP + DuckDuckGo MCP → incremental).
- **SillyTavern = persona/memory:** Character Cards (= GPTs/Poe bots) + World Info/Lorebooks (keyword-triggered
  injection) — cheap, ideal for uncensored single-user personas.
- **Build native, not embed:** none of these solve DokiDex's single-GPU **LLM↔media mutual exclusion** — a
  native shell that arbitrates the GPU (llama-swap evict ↔ SwarmUI) is the differentiator. → see design doc.

### 2.2 The one-click / declarative installer pattern
**Pinokio** (`comfyui.pinokio`) = a declarative JSON manifest orchestrating git-clone + isolated venv + pip +
model downloads. Validates DokiDex's [[prefer-self-contained-onbrand-installer]] direction.

### 2.3 Patterns DokiDex has already nailed (validation, not TODO)
Start+end-frame (→ **FLF2V**), director-mode camera (→ **camera compiler**), preset "effects" packs à la
Higgsfield (→ **effect-preset gallery**), Suno section/stem editing (→ **ACE-Step 1.5 stems**), Krea live
canvas (→ **Z-Image-Turbo scratchpad**). Net-new from the surveys: **TTS-Audio-Suite consolidation** + the
**face-ID / lip-sync / anime / in-image-text-edit** specialists above.

---

## 3. Highest-leverage features, ranked + tagged

1. **Chat/assistant surface** — ✅ backend exists; closes the Claude/ChatGPT/Grok gap. (design doc → approval)
2. **ACE-Step 1.5 swap** — ✅ low-risk in-family upgrade, big capability jump (stems/repaint/600 s/LoRA).
3. **TTS-Audio-Suite** — 🔶 one node → 15 voices + zero-shot cloning + RVC voice-conversion.
4. **Qwen-Image-Edit** (in-image text edit) — ✅ native ComfyUI.
5. **Anime pack** (Illustrious-XL v1.0 / Animagine XL 4.0) — 🔶 SDXL, trivial on 32 GB.
6. **PuLID-Flux** face identity — 🔶 the missing "consistent character" primitive.
7. **Wan 2.2 A14B GGUF** quality video tier — 🔶 SwarmUI's recommended best-local video.
8. **InfiniteTalk** lip-sync — 🔶🧪 the missing talking-head capability.
9. **FLUX.2 Klein** — 🔶 art-style/editing variety. **Nunchaku NVFP4** — 🔶 speed.
10. **LTX-2 / LTX-Video** — 🧪 sync-audio & fast-draft video; eval-gate (LTX-2 marginal on 32 GB).

---

## 4. Honest gaps (did NOT survive verification — flagging, not hiding)

- **Open video head-to-head incomplete:** only Wan 2.2 14B and LTX-2.3 are confirmed as the two 32 GB-fitting
  GGUF picks. **HunyuanVideo, Mochi-1, CogVideoX-5B** were not benchmarked against a measured 32 GB run.
- **Real-time canvas latency:** only Z-Image-Turbo's speed is confirmed; **StreamDiffusion / LCM-LoRA /
  SDXL-Turbo** live-loop latency + ComfyUI-StreamDiffusion integration remain unbenchmarked.
- **Lip-sync comparison:** InfiniteTalk is confirmed, but **MuseTalk / LatentSync / Hallo2-3 / SadTalker /
  Wav2Lip / Sonic / EchoMimic** produced no surviving ranked claim (quality/VRAM unconfirmed).
- **In-image-text head-to-head:** Qwen-Image/Edit confirmed strong, but a direct **Qwen vs FLUX.2 vs
  Ideogram** text-accuracy ranking is unverified.
- **Audio/voice platform census:** **Suno / Udio / ElevenLabs / Cartesia / PlayHT** open-vs-closed status
  produced no surviving claim. (Fetched-but-unverified: **Fish Audio appears to ship open weights** —
  `fishaudio/openaudio-s1-mini`, `fish-speech`.)
- **Consumer video sites:** only **Hailuo 2.3** confirmed proprietary and **LTX Studio** confirmed
  open-backed (LTX-Video); Runway/Pika/Kling/Luma/Veo/PixVerse/Vidu treated as proprietary-unknown,
  inferential not individually verified.

### Refuted this round (do NOT rely on)
- LTX-2 = "first open audio+video model in SwarmUI" (1-2); LTX-2.3 blanket "16 GB minimum" (0-3);
  Pony V7 "no ComfyUI support" (0-3 — it *does*); Z-Image-Turbo real-time "without quantization" (1-2).
- (Pass 1-2) "Higgs beats Chatterbox" on EmergentTTS-Eval (0-3); Chatterbox Perth-watermark un-removability
  (1-2); Recraft "definitively closed" (1-2, unsettled); "Krea exposes the same open checkpoints DokiDex
  runs" (1-2).

---

## 5. Key primary sources (per topic)

- **Chat / local-first:** docs.openwebui.com/features, docs.sillytavern.app, lmstudio.ai/docs/developer/openai-compat.
- **Image models:** github.com/mcmonkeyprojects/SwarmUI `docs/Model Support.md`, huggingface.co/black-forest-labs/FLUX.2-dev, huggingface.co/Tongyi-MAI/Z-Image-Turbo, mit-han-lab/nunchaku.
- **Video:** SwarmUI `docs/Video Model Support.md`, huggingface.co/Wan-AI/Wan2.2-TI2V-5B, QuantStack/Wan2.2-I2V-A14B-GGUF, Lightricks/LTX-2 + ComfyUI-LTXVideo, NVIDIA RTX video guide, zenn.dev/toki_mwc (5090 benchmark).
- **Anime:** huggingface.co/OnomaAIResearch/Illustrious-XL-v1.0, cagliostrolab/animagine-xl-4.0, purplesmartai/pony-v7-base.
- **In-image text:** QuantStack/Qwen-Image-GGUF, docs.comfy.org/tutorials/image/qwen/qwen-image-edit.
- **Audio:** ace-step/ACE-Step-1.5, github.com/diodiogod/TTS-Audio-Suite, resemble-ai/chatterbox, index-tts/index-tts, boson.ai Higgs Audio v3.
- **Specialists:** balazik/ComfyUI-PuLID-Flux, MeiGen-AI/InfiniteTalk.

> Full per-claim evidence + votes are in the three workflow task outputs for this session
> (`wqo82zyva`, `w2iuscovm`, `w5xd7rrjv`).
