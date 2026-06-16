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
- **Update (2026-06-15, web recon):** the anticipated upstream update has landed. **LTX-2.3** (22B,
  Mar 2026) shipped full open weights, and **community GGUF quants now exist** (`unsloth/LTX-2.3-GGUF`,
  `unsloth/LTX-2-GGUF`) with ready ComfyUI audio+video workflows (`awesome-ltx2`). Official min is
  **32 GB+ VRAM — i.e. exactly the RTX 5090** (~25 s for a 720p/24fps/4 s clip; 8 s spills past 32 GB
  into weight streaming). It's the first **open-source one-pass audio+video** model and adds spatial
  upscalers. LTX-2's old blocker (no loadable checkpoint) is plausibly resolved via the GGUF release —
  **re-recon SwarmUI's LTX2 path against an `unsloth/LTX-2.3-GGUF` checkpoint** before writing it off;
  if it loads, this becomes DokiGen's Tier-2 keystone (native synced A/V, no S2V injection blocker).
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

## Frontier-model watchlist (2026-06-16) — eval-gated challengers + cheap add-ons

Verified candidates against the new **Z-Image Base** default (now the `doki gen` image default —
`z_image_bf16`, 35 steps, real CFG 4.5 + negatives; Turbo is the `-Fast` tier). Per the project's
**swap-only-on-a-measured-win** rule, none of these are defaults: each gets a **blind short-prompt
bake-off vs the current default**, and is adopted **only on a measured win**. The cheap add-ons that
ride alongside (LoRAs/upscalers) don't replace the default, so they only need a "is it better when
asked for it" check. Already addressed this session is marked **✅**.

### Image — eval-gated challengers (run a blind short-prompt A/B vs Z-Image Base; adopt only on a measured win)

| Candidate | Why it might win | Download | VRAM | License | A/B gate → decision |
|---|---|---|---|---|---|
| **FLUX.1-Krea-dev** | Best-in-FLUX-family **natural skin** (kills the "plastic" look); best for portraits. Guidance-distilled → real CFG stays **1**, push the **FluxGuidance** knob ~3.5–4.5. | fp8 `huggingface.co/Clybius/FLUX.1-Krea-dev-scaled-fp8` (orig `black-forest-labs/FLUX.1-Krea-dev`); needs **T5-XXL + CLIP-L + flux ae VAE**. Loads via the standard **FLUX.1-dev path** in SwarmUI. | fp8 **~12 GB** | **NON-COMMERCIAL** (FLUX.1-dev non-comm) | Blind portrait/skin short-prompt set vs Z-Image Base → **adopt only if skin realism wins**; else keep Base, archive weights. License blocks any commercial use regardless of win. |
| **Qwen-Image-2512** | Best open **prompt-adherence + in-image TEXT**; helps complex compositions where Z-Image is weaker. True-CFG ~4 **with a negative prompt**. | GGUF `huggingface.co/unsloth/Qwen-Image-2512-GGUF` — **Q5_K_M ~15 GB / Q6_K ~16.8 GB / Q8 ~21.8 GB** (all fit 32 GB). Reuses the **Qwen2.5-VL TE** (already on disk from the edit model) + **qwen_image VAE** (already on disk). | ~15–21.8 GB | **Apache-2.0** | Blind complex-composition + in-image-text short-prompt set → **adopt for text/adherence only on a measured win**; commercially clean either way. |
| **FLUX.2-dev** | — (FLUX-family adherence) | — | **fp8 ~32 GB** | non-comm | **OFF the table** — fp8 ~32 GB is VRAM-tight on the 5090 (same wall decisions.md hit for the Wan 14B dual-expert). Do not download. |

### Image — cheap add-ons (architecture-specific; ride alongside the default, no swap)

- **✅ Realism LoRA (now wired via the `-Realism` flag):** `-Realism` appends `<lora:Z-Image-Realism:0.7>`.
  `setup.ps1 -Models full` fetches it from HF **`suayptalha/Z-Image-Turbo-Realism-LoRA`** (Apache-2.0,
  public/unauthenticated `resolve/` URL — the same scriptable pattern as the Wan-Lightning LoRAs) and saves it
  as **`Z-Image-Realism.safetensors`** in `Models\Lora`. Civitai alternatives to A/B (**token-gated**, so not
  auto-fetched — needs `CIVITAI_API_TOKEN`): **2268008** (Realistic Snapshot) / **2395852** (Radiant Realism
  Pro) — drop either into `Models\Lora` renamed `Z-Image-Realism.safetensors` to swap. All
  **architecture-specific to Z-Image** — generic SDXL detail LoRAs / negative embeddings will **NOT** load.
  Gate: A/B `-Realism` on vs off on a skin/photo short-prompt set → keep opt-in (default-off); promote a
  specific LoRA into the default recipe only on a measured win.
- **4x-NMKD-Siax upscaler** (~67 MB) for skin/photo — **gentler than UltraSharp**; selectable via
  `refinerupscalemethod`. **On-disk filename uses an underscore: `4x_NMKD-Siax_200k.pth`** — confirm the
  exact name before wiring, because SwarmUI's selector string is **filename-exact** (the existing 4x-UltraSharp
  is exposed as `model-4x-UltraSharp.pth`, i.e. a `model-` prefix on the on-disk `4x-UltraSharp.pth`). Gate:
  A/B vs UltraSharp on a face/skin upscale → adopt as the photo-preset upscaler only if it's visibly gentler/better.
- **Backlog ComfyUI nodes** (higher-effort, **explicitly-invoked** like the WanFoley workflow — not on by default):
  - **Detail Daemon** (`Jonseed/ComfyUI-Detail-Daemon`) — **CFG-1-safe** detail for distilled Turbo; manipulates
    the **sigma schedule, not CFG** (so it works where negatives/CFG are inert). Gate: A/B on a `-Fast`/Turbo gen.
  - **Ultimate SD Upscale** (`ssitu/ComfyUI_UltimateSDUpscale`) — tiled img2img to **4K**. Gate: A/B vs the
    current `-Refine` hi-res-fix on a single hero image (quality vs time).
  - **SUPIR** — heavy diffusion restoration upscaler; **needs an SDXL base**; **single-hero-shot feasible on 32 GB**.
    Gate: one-shot quality A/B vs UltraSharp/Ultimate SD Upscale; adopt only as an explicit "restore this one shot" path.

### Video — eval-gated / recon

- **Default stays Wan 2.2 TI2V-5B**, now tuned (**cfg 3.5, Sigma Shift 8, uni_pc/simple**; native res
  **1280×704** available; i2v lengthened to **49f**). **LTXV-2b-distilled stays the `-Fast` tier** (optional
  res bump toward its native **1216×704**). No A/B needed — these are the confirmed defaults.
- **Wan 2.2 14B A14B — do NOT default.** The fp8 dual-expert **OOMs >300 s past 32 GB** in SwarmUI's StepSwap
  (state held across the high→low handoff + resident ~6.3 GB umt5 + activations) — **proven live** (decisions.md
  2026-06-14). The **only zero-OOM 14B route** worth an eval-gated A/B is **GGUF Q4_K_M** (~9.6 GB/expert,
  `QuantStack/Wan2.2-T2V-A14B-GGUF`, **native SwarmUI GGUF loader**):
  - base = **HIGH-noise** GGUF, **Refiner = LOW-noise** GGUF, **Refiner Method = Step-Swap**, **Refiner Control
    Percentage 0.5**.
  - pair the already-downloaded **Wan22-Lightning 4-step LoRAs** (HIGH strength **~0.7** / LOW **1.0** — community
    practice, **not** a model-card spec), **steps 8 (4+4)**, **16 fps** (14B native, **NOT 24**).
  - Gate: blind short-prompt A/B vs the tuned 5B → **adopt only on a measured quality win at acceptable time**;
    **record the A/B result either way** (a clean negative is as useful as a win here).
- **LTX-2.3 GGUF** (`unsloth/LTX-2.3-GGUF`, **22B**, **one-pass synced audio+video**) — **recon backlog.** SwarmUI
  already detects the LTX-2 class (`isLtxv23`) but previously had **no loadable checkpoint** — **check whether its
  native loader accepts the GGUF** (the plausible fix for the old "no checkpoint" blocker; see Tier 2's 2026-06-15
  note). Floor is **32 GB VRAM (exactly the 5090)** → stay **≤720p / ≤4 s** to stay in-VRAM. License: **non-Apache
  LTX-2 Community License** — **check terms before commercial use.** Decision: if the GGUF loads, this becomes the
  Tier-2 keystone (native synced A/V, no S2V injection blocker); if not, it stays parked until a SwarmUI/ComfyUI update.
