# DokiDex — Capabilities (everything we support)

*The definitive, current map of what DokiDex can do. Generated 2026-06-21 from a full codebase sweep. For the friendly intro see `wiki/Home.md`; for exact media API payloads see `wiki/11-media-recipes.md`; for design rationale see `decisions.md`.*

**DokiDex** is a fully-local, uncensored, single-user AI studio on one machine (32 GB RTX 5090 + 64 GB DDR5 + i9-14900KS, Windows 11, no Docker). It does coding-assistant work, conversational chat, and image/video/music/voice generation — entirely offline. The GPU is **mutually exclusive** between the LLM group and the media group (one resident at a time); the app coordinates the flip.

---

## 1. The Studio — a guided web app (`http://127.0.0.1:5111`)

A single-page app with a guided **Home** command center and 11 capability areas, grouped **Make / Talk / Manage**:

| View | Group | What you can do |
|---|---|---|
| **Home** | — | Self-guiding front door: state-aware capability cards, live readiness badges, clickable starters, mini-guides, recent-work thumbnails, a type-anything quick-start, and a GPU/mode meter |
| **Create** | Make | Generate image / video / music / edits — with a live sketch canvas, ControlNet stacking, init-image + IP-Adapter style-ref, inpaint/outpaint, upscale/hi-res-fix, LoRA mixing, aspect/seed/count, batch + series, explore-variations |
| **Director** | Make | Turn a script or idea into an ordered shot list (LLM), then render each shot |
| **Flow** | Make | Chain generation steps into a node graph (DAG) and run them in dependency order |
| **Scene** | Make | Compose multi-character scenes with isolated per-region prompts + a 3D blockout→depth ControlNet |
| **Chat** | Talk | Local uncensored assistant: tools, vision, documents (RAG), long-term memory, **generate/edit images in-thread**, TTS readback, personas, lorebooks |
| **Cast** | Talk | Build reusable character cards (personas) for chat + scenes |
| **Voice** | Talk | Text-to-speech with cloneable voices; multi-speaker dialogue with performance tags; pronunciation dictionary |
| **Library** | Manage | Browse / search / rate / favorite / remix everything generated; variation lineage forest; trash + recovery; saved searches; palette tools; story-bible export |
| **Models** | Manage | Install / switch / delete local image / video / LLM models |
| **Status** | Manage | Live service health, the GPU meter (VRAM/util/temp/watts/fan), and the Agent/Coexist/Media mode switch |
| **Memory** | Manage | Review / add / edit / delete the long-term facts the assistant recalls in every chat |

---

## 2. Media generation

### Generation kinds (11)
`image` · `video` · `music` · `edit` (instruction inpaint) · `i2v` (image→video) · `foley` (video+synced SFX) · `faceid` (InstantID) · `pulid` (PuLID, FLUX-based) · `infinitetalk` (audio-driven talking video) · `latentsync` (lip-sync existing video) · `speech` (TTS-Audio-Suite, 15 engines).

### Per-gen options / modifiers
Prompt (with `@references` + `__wildcards__`), Seed, Count, Model (`auto` router / explicit / default), Fast / Quality tiers, Raw (skip rewriter), Negative, Workflow override · **Image:** Upscale, Refine (hi-res-fix), Upscaler engine, Face refine, Realism LoRA, LoRA mixer, promptable Segment refine, Aspect, InitImage (img2img), Strength, MaskImage (inpaint), ControlNets (1–3 stacked), IP-Adapter Reference + weight, seamless Tile · **Video:** EndImage (FLF2V keyframe), Interpolate (RIFE/FILM/GIMM ×2–8) · **Music:** Lyrics, Duration, Bpm · **Audio-driven:** Audio clip · **Ephemeral** (throwaway live-canvas render).

### Post-process & media tools
Per-card refine (face / hi-res / upscale / vary) · one-click effect presets (anime, noir, cyberpunk, watercolor, …) · extract video frame · join clips (ffmpeg) · audio stem separation (Demucs) · dominant-color palette + recolor · reverse-prompt **Describe** + adherence **Verify** (vision LLM) · steerable prompt **Rewrite** · style-chip composition · gallery import.

### Advanced workflows
Live real-time sketch→image canvas · ControlNet stacking · multi-character regional scenes · camera-cinematography compiler · node-graph DAG flow · shot-list director · image series/grid · batch CSV + saved recipe pipelines · seed-varied explore grid · 3D blockout depth rasterizer · in-app **LoRA training** (kohya) · SAM click-to-mask · model-compare grid · pitch-deck / story-bible HTML export.

### Model catalog (defaults **bold**)
- **Image** (default **Z-Image Base**): Z-Image Turbo (fast), Chroma HD, FLUX.2 Klein 4B (+NVFP4), Illustrious-XL / Animagine-XL-4 (anime), Qwen-Image (in-image text; GGUF + NVFP4).
- **Video** (default **Wan 2.2 TI2V-5B**, 720p/24fps): Wan 2.1 1.3B (lean), LTXV-2b (fast), Wan 2.2 A14B dual-expert GGUF (quality).
- **Edit** (default **Qwen-Image-Edit 2511**). **Music** (default **ACE-Step 1.5 Turbo**; XL quality). **Upscale**: 4x-UltraSharp / 4x-AnimeSharp.
- Gated workflows (install-on-demand): InstantID, PuLID, InfiniteTalk, LatentSync, WanFoley, TTS-Audio-Suite (15 engines incl. IndexTTS2/Higgs/VibeVoice/RVC).

---

## 3. Chat & assistant

- **Curated tools (5):** `search_library` (your media), `web_search` (DuckDuckGo), `code_search` (semantic RAG over this repo), `generate_image` (queue a gen), **`edit_image`** (refine/img2img sibling). Bounded agentic loop (≤4 tool-hops, 4-min cap, temp 0.3 for tool accuracy).
- **Context injection:** `[Memory]` (durable cross-chat facts, sqlite+FTS5) · `[World Info]` (keyword-triggered lorebooks) · `[Documents]` (per-conversation + named-library RAG over .txt/.md/PDF/DOCX) · **vision** (attach Library images → auto-forces the Qwen3-VL model) · 20-turn history window · personas (system + user-identity + few-shot + voice).
- **Chat→media round-trip:** the assistant generates *and edits* images in-thread — the tool queues a gen, a GPU-flip coordinator evicts the LLM, renders over SwarmUI (streaming progress + preview), flips back, and the final image appears inline in the conversation.
- **Knowledge bases:** per-conversation doc attach + reusable named KBs, exportable as portable `.ddkb` envelopes (vectors included), with a global default-KB auto-attach.
- Streaming (SSE) · edit/regenerate/branch/delete turns · TTS readback of replies.

---

## 4. Voice

- **TTS:** Chatterbox (default, expressive, **zero-shot voice cloning**) + Kokoro (gated, fast, preset voices). OpenAI-compatible `/v1/audio/speech`, coexists with the chat LLM.
- **Multi-speaker dialogue:** a tagged script (`[excited]`, `[whispers]`, …) routed per-speaker to assigned voices, concatenated to one clip.
- **Pronunciation dictionary** (word-boundary alias substitution before synthesis). **STT:** Parakeet (gated). All output lands in the Library.

---

## 5. Models served (LLM)

Served via **llama.cpp (b9616, CUDA 13.3) + llama-swap** on `:8080`, **OpenAI- and Anthropic-compatible** (native `/v1/messages`). Hot-swapped tiers:
- **coder-fast** (daily driver) — Qwen3-Coder-30B-A3B-Instruct Q4_K_XL, 128k ctx, fully on GPU (~26 GB).
- **coder-big** (heavy) — gpt-oss-120b MoE, GPU+CPU hybrid offload.
- **vision** — Qwen3-VL-8B-Instruct + mmproj (~7 GB; gated).
- **coder-fast-lite** — 64k ctx, coexists with the FIM autocomplete server.
- Bake-off candidates (gated, eval-gated before promotion): Qwen3-Coder-Next-80B, GLM-4.7-Flash, others.

---

## 6. Local infrastructure

- **Services:** `llama-swap` (:8080) · `fim` autocomplete (:8012) · `embed` RAG (:8090, CPU) · `tts` Chatterbox (:8004) · `kokoro` (:8006) · `stt` Parakeet (:8005) · `media` SwarmUI (:7801) · `prompt-rewriter` (:8013).
- **GPU mode model:** **agent** (LLM + TTS + STT + embed) / **coexist** (LLM + FIM + embed) / **media** (SwarmUI + rewriter) — mutually exclusive; switching evicts the opposite group. `generate_image` *queues* so chat is never evicted mid-turn; the flip happens on the round-trip drain or an explicit mode switch.
- Native status probe (pidfiles + nvidia-smi + endpoints, no PowerShell on the hot path); per-service start/stop/restart; model warm-load.

---

## 7. Control & operations

- **WPF control panel** (`doki panel` / `DokiDex.lnk`): native .NET 9 cockpit — service cards, GPU trust-meter, mode switcher (with 32 GB-headroom eviction confirm), live per-service log tails, per-modality ⚡ smoke tests, coder model-swap chips, update badges + **in-app auto-updater** (self-contained single-file exe).
- **Web app** (DokiGen Studio, `:5111`): the Studio in §1 — also hosted in-process by the panel.
- **Installer:** self-contained single-file `.exe` (GitHub releases) with a setup wizard; `control.bat` (builds the panel + shortcut); `setup.ps1` (headless bootstrap with `-Media`/`-Tts`/`-Stt`/feature flags).
- **CLI** (`doki.ps1`): `up [agent|coexist|media]`, `down`, `status`, `gen "<idea>"`, `panel`, `verify`, `doctor`, `test`.

---

## 8. Design principles

Single-source recipe (the `doki-gen.ps1` body + `GenRequest` contract drive both the web path and the CLI 1:1) · pure unit-tested seams (recipe args, prompt assembly, readiness, routing, the render-coordinator sequencing — all GPU-free) · graceful degradation (every sidecar/tool degrades to empty/error, never crashes the loop) · GPU arbitration by queue-and-flip · loopback-only with host-header allowlist + CSRF on state-changing verbs · verify-on-GPU before claiming media features work.
