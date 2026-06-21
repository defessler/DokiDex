# Deep research — chat→media round-trip + local-model refresh (2026-06-21)

Multi-agent dive: **107 agents, 24 primary sources, 119 claims → 25 adversarially verified → 24 confirmed, 1 refuted.** Aimed at DokiDex's next phase. Full raw report archived in the run's task output.

## Headline
The **chat→media round-trip is feasible TODAY** over DokiDex's existing SwarmUI + OpenAI/Anthropic-compatible stack — every piece except one is a primary-source-verified pattern. The single exception (and the must-prototype risk) is the **GPU-flip coordinator**: no external app faces a single GPU mutually-exclusive between the LLM and media groups, so that sequencing is DokiDex-specific and unsourced.

## The round-trip design (verified patterns → DokiDex blueprint)
1. **One agentic tool, native tool-calling.** Expose `generate_image` (+ a new `edit_image`) as native function-calling tools the local LLM invokes directly — exactly how OpenAI's Responses API, Open WebUI (`backend/open_webui/tools/builtin.py`), and SillyTavern do it; the latter two over LOCAL OpenAI-compatible backends (our llama.cpp/llama-swap). Qwen3-Coder-30B is a strong tool-caller, mitigating the "LLM might not call the tool" caveat. *(We already have `generate_image` — add `edit_image`.)*
2. **Async render surfaced via SwarmUI's WebSocket.** `GenerateText2ImageWS` streams `overall_percent` + base64-JPEG **preview** images + the final image path — exactly the data a chat-thread `queued → warming up (preview) → done` UI needs. Synchronous `POST /API/GenerateText2Image` (after `GetNewSession`) is the simpler fallback. **This is the renderer wiring the foundation is missing.**
3. **Multi-turn continuity via seed-carrying text tokens.** The **ComfyInject** pattern (AGPLv3): an outbound interceptor rewrites prior images into compact `[[IMG: prompt | seed]]` text tokens so even non-vision turns retain identity/continuity ("same character, new pose"). Re-inject prompt + locked seed; pair with client-side conversation/image-ID threading (OpenAI's `previous_response_id` analogue, implemented locally).
4. **GPU-flip coordinator (the new load-bearing piece).** On a tool call: durably queue (already built) → the tool returns immediately with `queued` (LLM turn completes, releases the GPU) → an out-of-band coordinator runs **evict-LLM → load-media → stream-render (WS preview into the thread) → unload-media → reload-LLM** → inject the final image inline. This async, out-of-turn surface is the ONLY design that respects the exclusivity. **Must prototype + measure the flip latency first.**

## Model recommendations (32GB RTX 5090, all verified)
- **Image — ADD FLUX.2-dev (4-bit) for edit/identity/in-image-text.** `black-forest-labs/FLUX.2-dev` (32B) via the **official 4-bit checkpoint `diffusers/FLUX.2-dev-bnb-4bit`** (~18–24GB). Full precision ~64–80GB (won't fit); FP8 ~32–35GB is marginal/offload-dependent. Beats Z-Image-Turbo specifically on instruction-based editing, multi-image identity, and in-image text. **Tag: gated-on-optional-install** (needs the 4-bit quant).
- **Image — KEEP Z-Image-Turbo as the fast first-pass.** 6B, ~16GB incl. text encoder, 8-NFE (~sub-2s on a 5090). It's the speed bar. Run a **two-model image strategy** (Turbo for speed, FLUX.2-4bit for heavy edits) — fine, since only one media model is resident at a time.
- **Video — KEEP Wan2.2 TI2V-5B** (confirmed comfortable on 32GB; 5s 720p/24fps in <9 min). Optional quality upgrade: a **quantized 14B Wan2.2 I2V** (fp8/GGUF, e.g. `QuantStack/Wan2.2-I2V-A14B-GGUF`) — **gated-on-optional-install**.
- **Reference-only (NOT local): Nano Banana Pro = Google Gemini 3 Pro Image** — closed, API-only. Its "up to 5 people / blend 14 images" identity envelope is the *bar* our local FLUX.2 / Qwen-Image-Edit pipeline is measured against, not a model we can run. (Its "localized region editing" claim was **refuted 0-3** — don't cite it; rely on the verified `edit_image`/inpaint precedents.)

## Recommended next build
Complete the **generate-from-chat round-trip** on the existing foundation: add `edit_image` + SwarmUI `GenerateText2ImageWS` progress/preview surfacing + inline final-image injection + the **GPU-flip coordinator** (prototype-and-measure first — it's the architectural risk). Then a focused **follow-up research dive on FOCUS 3** before touching those models.

## Open gaps (NOT covered — budget-dropped; need a follow-up dive)
- **FOCUS 3 entirely unverified:** lip-sync/talking-avatar (LatentSync / Sonic / Hallo2 / MuseTalk — sources fetched, no claim survived verification), real-time/latent-canvas (StreamDiffusion / LCM / Lightning), LTX-2 vs Wan.
- **Audio/music + LLM refresh unverified:** ACE-Step / Stable Audio successors; Qwen3 / gpt-oss / Qwen-VL / abliterated refresh.
- **GPU-flip latency:** real evict→load→render→reload seconds on the 5090 (warm-cache / pinned-VRAM strategy?).
- **Local identity fidelity:** how close FLUX.2-4bit + Qwen-Image-Edit gets to the Nano Banana envelope; does 4-bit degrade identity/text vs FP8?

## Primary sources
OpenAI image-gen API · Open WebUI docs+source · SillyTavern docs · SwarmUI `API.md`+`T2IAPI.md` · ComfyInject · HF cards (FLUX.2-dev, Z-Image-Turbo, Wan2.2-TI2V-5B) · Google/DeepMind (Nano Banana Pro). Full verified claim set + votes in the archived run output.
