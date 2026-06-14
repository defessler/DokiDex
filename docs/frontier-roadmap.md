# DokiCode Frontier-Gap Roadmap

What's missing to close the distance to the two frontier bookends — **code = the Mythos
bar** (Anthropic's frontier reasoning/code model) and **media = the Sora-2 bar** — ranked by
feasibility on a single 32 GB RTX 5090, native Windows, no Docker. From the multi-agent
gap-analysis workflow; every item checked against the installed repo.

## Tier 1 — quick wins (mostly zero or small downloads)

| # | Gap closed | How | Effort | Downloads |
|---|---|---|---|---|
| 1 | **Image-to-Video** (animate a still) | Wire the *already-installed* Wan 2.2 TI2V-5B via `Wan22ImageToVideoLatent.start_image`. Fork `media-assets/WanFoley.json` → `WanI2V.json`: add a `LoadImage` feeding node 5's `start_image`; default 1280×704, length 121, fps 24. Fix the wrong VAE comment in `setup.ps1`. | **S** | **none** |
| 2 | **Music / song generation** | ACE-Step 1.5 — nodes already ship in `comfy_extras/nodes_ace.py`; add an `AceStep.json` custom workflow + model download. | M | ~3 GB |
| 3 | **Precise image editing** (instruction edits, inpaint) | Qwen-Image-Edit-2511 — SwarmUI-native; inpaint is free once the model is present. | M | ~12–20 GB |
| 4 | **Upscaling / detail** | 4x-UltraSharp into `Models/upscale_models` (currently empty); SwarmUI exposes it as a refiner/upscale step. | S | ~0.07 GB |
| 5 | **Speech-to-text** (the missing input modality) | Parakeet (NVIDIA) via `onnx-asr` in a FastAPI service on `:8005` (`start-stt.ps1`, `group=llm`, CPU EP first then CUDA). Mirrors the TTS service pattern. | M | ~2 GB |

The **lead item is #1 (I2V)** — it is the single highest-value, zero-download capability
the box is missing, and the model is already on disk.

## Tier 2 — substantial, single big download each

- **Talking-head / lip-sync** — Wan2.2-S2V-14B (speech-to-video). Heavy (14B class), so
  it runs in the media GPU group like the other big video models; quality bar for
  audio-driven avatars.
- **Photoreal restoration upscale** — SUPIR (diffusion upscaler) for the hardest detail
  recovery; larger and slower than 4x-UltraSharp, used selectively.
- **One-pass video+audio** — LTX-2.3 as an alternative to the Wan→Foley two-step (joint
  generation rather than V2A post-hoc); evaluate against Wan 2.2 quality before adopting.

## Tier 3 — cloud-only (state plainly; do not pretend to match locally)

- Native **4K / 60 fps** and **10–25 s** coherent video (Wan is ~5 s native at 480–720p;
  longer drifts, 1080p+ needs upscales).
- **World models / persistent-state** video and the hardest physics + multi-shot identity.
- **Frontier raw reasoning** at the Mythos tier — a single 32 GB card cannot host that
  class of model; the local code stack closes the gap with *workflow*, not raw model size.

## Code side — toward the Mythos bar

- **120B routing** — route hard tasks to `coder-big` (gpt-oss-120b, CPU-MoE offload) and
  keep `coder-fast` (Qwen3-Coder-30B) as the daily driver; llama-swap already hosts both.
- **RAG / memory MCP** — a retrieval + persistent-memory MCP server so the agent carries
  project context across sessions.
- **Multi-agent orchestration** — the workflow pattern (parallel research + adversarial
  verification + synthesis) is the biggest lever on *effective* code quality without a
  bigger model.

## Honest Sora-2 gap (for docs)

Stills match/exceed Sora-2 keyframes; short 5 s 480–720p Wan 2.2 clips are cinematic and
coherent (a modest visual gap); speed/cost/control/uncensored win outright. Stays short on
1080p+ and 10–25 s length, the hardest physics + multi-shot identity, and perfectly synced
lip movement (Foley is V2A post-hoc; Wan2.2-S2V in Tier 2 is the lip-sync answer). The
always-on prompt-rewriter is what manufactures the "just works from one sentence" feel.
