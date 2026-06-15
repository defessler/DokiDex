# DokiDex Frontier-Gap Roadmap

What's missing to close the distance to the two frontier bookends — **code = the Mythos
bar** (Anthropic's frontier reasoning/code model) and **media = the Sora-2 bar** — ranked by
feasibility on a single 32 GB RTX 5090, native Windows, no Docker. From the multi-agent
gap-analysis workflow; every item checked against the installed repo.

## Tier 1 — quick wins (mostly zero or small downloads)

| # | Gap closed | How | Effort | Downloads |
|---|---|---|---|---|
| 1 | **Image-to-Video** (animate a still) ✅ **DONE** | SwarmUI's **native** `videomodel` pipeline animates the already-installed Wan 2.2 TI2V-5B — **no custom workflow needed** (live testing corrected the original "fork WanFoley.json" plan). Also fixed the wrong VAE comment in `setup.ps1`. | **S** | **none** |
| 2 | **Music / song generation** ✅ **DONE + live-verified** | ACE-Step **1.5** is **SwarmUI-native** (class `ace-step-1_5`; audio VAE + qwen ace15 text-encoders auto-download) — no custom workflow. Verified: `textaudiostyle`/`bpm`/`duration` → a real 12s 48kHz-stereo MP3. `setup.ps1` pulls XL base (quality) + turbo (fast) + VAE; guarded `verify.ps1` smoke. | M | ~15 GB |
| 3 | **Precise image editing** (instruction edits, inpaint) ✅ **DONE + live-verified** | Qwen-Image-Edit-2511 (`fp8mixed`, class `qwen-image-edit-plus`) — SwarmUI-native: `model` + init image + an instruction prompt edits the image (verified: red→green apple, composition preserved). Wired in `setup.ps1`; guarded `verify.ps1` smoke. | M | ~30 GB |
| 4 | **Upscaling / detail** ✅ **DONE + live-verified** | 4x-UltraSharp in `Models/upscale_models`. Verified 512→1024 (2×). Trigger: SwarmUI's Refiner-Upscale only fires when `refinermethod`+`refinercontrolpercentage` are set (`PostApply` + control `0` = pure upscale, no refine pass) with `refinerupscalemethod=model-4x-UltraSharp.pth`. Guarded `verify.ps1` smoke. | S | ~0.07 GB |
| 5 | **Speech-to-text** (the missing input modality) ✅ **DONE + live-verified** | Parakeet (NVIDIA) via `onnx-asr` FastAPI on `:8005` — `serving/stt-server.py` + `start-stt.ps1`, registered in doki (`group=llm`, agent profile, CPU EP). Installed via `setup.ps1 -Stt`; verified a real TTS→STT round-trip ("…quick brown fox jumps over the lazy dog"). Guarded `verify.ps1` smoke. | M | ~2 GB |

The **lead item is #1 (I2V)** — the single highest-value, zero-download capability the box
was missing, now **shipped + live-verified** (3 real 25-frame mp4s generated).

### I2V recipe (SwarmUI native `videomodel`, live-verified 2026-06-14)

`POST :7801/API/GenerateText2Image`. The **main `model`** makes the first frame; the
**`videomodel`** (the 5B, class `wan-2_2-ti2v-5b`) animates it. The I2V step only runs when
`videosteps`/`videocfg`/`videoresolution` are also supplied (the missing piece in the first
attempt). Output array contains the first-frame PNG **and** the MP4.

```jsonc
{ "session_id": "...", "images": 1, "prompt": "...",
  "model": "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors",   // first frame (any image model)
  "videomodel": "wan2.2_ti2v_5B_fp16.safetensors",        // the animator
  "videoframes": 25, "videosteps": 20, "videocfg": 3.5,
  "videofps": 24, "videoresolution": "Image", "videoformat": "h264-mp4" }
```

To animate an **existing still** instead of a fresh frame, add `"initimage": "<base64>"` +
`"initimagecreativity": 0` (the still passes through unchanged as frame 1). Why not a custom
workflow: hand-authored `SwarmInputImage` nodes need editor-generated `custom_params`
metadata to receive the image, and `SwarmLoadImageB64` + `${initimage}` gets a data-URL that
its raw `b64decode` corrupts — the native path sidesteps both. Verify smoke added to
`verify.ps1` (skips clean when the 5B isn't installed).

## Tier 2 — substantial, single big download each

SwarmUI native-support recon (`T2IModelClassSorter.cs`, 2026-06-14): **LTX-2.3 IS native**
(`isLtxv23` detects `text_embedding_projection.audio_aggregate_embed.weight` — it has
audio-to-video attention built in), so it's tractable like Qwen/ACE were. **Wan2.2-S2V has
NO registered SwarmUI class** — it would need a custom ComfyUI workflow (harder/uncertain,
cf. the I2V custom-workflow dead-end).

Deeper recon (2026-06-14): the ComfyUI **Wan-S2V nodes DO exist** (`WanSoundImageToVideo` +
`AudioEncoderLoader` in `comfy_extras/nodes_wan.py`/`nodes_audio_encoder.py`) — but with no
SwarmUI native class, S2V needs a hand-authored custom workflow, which hits the **same
ref-image/audio base64-injection blocker** that killed the I2V custom workflow (`${initimage}`→
data-URL corrupts `SwarmLoadImageB64`; `SwarmInputImage` needs editor-generated `custom_params`).
So S2V is effectively **blocked pending native SwarmUI support** (don't sink a 14-28 GB download
into a known dead-end). The LTX-2 *audio* model is a separate newer release (the `Lightricks/
LTX-Video` repo is the video-only 0.9.x line, up to 0.9.8) — a speculative 14-26 GB pull of
uncertain lip-sync value.

- **Fast video** ✅ **DONE + live-verified:** LTXV-2b-0.9.8-distilled (~6 GB, SwarmUI-native, class
  `lightricks-ltx-video`, T5 auto-downloads). Verified 97 frames 768×512 in ~36s — a near-real-time
  *speed* option below Wan 2.2's quality. Wired in `setup.ps1`; guarded `verify.ps1` smoke.
- **Talking-head / lip-sync (blocked):** Wan2.2-S2V-14B — highest *unique* value but blocked by
  the custom-workflow injection limitation above until SwarmUI adds a native S2V class.
- **One-pass A/V (attempted → blocked):** LTX-2 19B (`lightricks-ltx-video-2`) — SwarmUI *detects*
  the class, but **can't load it** in this build: the diffusion model exists only as Kijai's
  `transformer_only` safetensors, and SwarmUI's LTX2 path errors *"requires the safetensors checkpoint
  format currently due to comfy limitations"* (Comfy-Org hosts only the LTX-2 text-encoders/loras, no
  loadable checkpoint). Nascent support — revisit after a SwarmUI/ComfyUI update. (The 20 GB test
  download was reclaimed.) So the audio-video frontier (S2V, SUPIR, LTX-2) is **fully mapped: all three
  blocked** — by custom-workflow injection (S2V/SUPIR) or nascent loader support (LTX-2), not VRAM.
- **Photoreal restoration upscale:** SUPIR — diffusion upscaler beyond 4x-UltraSharp.

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
