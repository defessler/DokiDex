# Media & Speech Recipes — exact local API calls

Every capability, with the precise (often non-obvious) request that actually works on this
setup. All verified live (`doki verify` 17/17). Media calls go to SwarmUI on `:7801`; speech to
the TTS/STT services; memory via the MCP tools.

> Switch the GPU first: **`.\doki.ps1 up media`** for image/video/audio, **`up agent`** for
> chat/speech. They're mutually exclusive on 32 GB.
>
> **One-liner:** `.\doki.ps1 gen "<idea>" [-Video|-Music|-Edit|-I2v|-Foley|-FaceId|-Pulid|-InfiniteTalk|-LatentSync] [-Fast] [-Upscale] [-Refine] [-Face] [-Realism]`
> wraps the calls below — it picks the recipe, wraps the idea in `<mpprompt:…>` for the `:8013` rewriter, POSTs
> to SwarmUI, and opens the result. The tables here are the underlying API; `doki gen` is the shortcut.

## Image & video (SwarmUI `POST :7801/API/GenerateText2Image`)

Every call needs a session: `POST :7801/API/GetNewSession` → `{ "session_id": ... }`, then put
that `session_id` in the body. Output paths come back in `images[]`.

| Capability | Key body fields |
|---|---|
| **Image** (Z-Image Base — quality default) | `model=z_image_bf16.safetensors, steps=35, cfgscale=4.5, width/height=1024, sampler=dpmpp_2m, scheduler=karras, negativeprompt="blurry, lowres, …"`. **-Fast** swaps to Turbo: `model=SwarmUI_Z-Image-Turbo-FP8Mix.safetensors, steps=8, cfgscale=1` (CFG-1 distilled → negatives inert). |
| **Text→video** (Wan 2.2 5B) | `model=wan2.2_ti2v_5B_fp16.safetensors, textvideoframes=49, steps=20, cfgscale=3.5, width=832, height=480, videofps=24, videoformat=h264-mp4, sampler=uni_pc, scheduler=simple, sigmashift=8` (Sigma Shift 8 = the 5B's tuned flow setting; native res is 1280×704). **-Quality** swaps to the GATED Wan 2.2 A14B GGUF dual-expert tier: `model=Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf` (base = HIGH-noise expert) **+** `refinermethod=StepSwap, refinermodel=Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf, refinercontrolpercentage=0.5` (LOW-noise expert via SwarmUI's StepSwap — wiring sourced from SwarmUI's *Video Model Support* doc), `cfgscale=5, sigmashift=8`. The 5B stays the video default; A14B is opt-in via -Quality. ⚠ on-GPU: the `refinermodel` body key + steps/sampler + 32GB fit + the city96 GGUF node are the labeled remaining confirms. |
| **Fast video** (LTXV) | `model=ltxv-2b-0.9.8-distilled.safetensors, textvideoframes=97, steps=8, cfgscale=1, width=768, height=512, videofps=24` (T5 auto-downloads first run) |
| **Image→video** (animate a still) | `model=<any image model>` **+** `videomodel=wan2.2_ti2v_5B_fp16.safetensors, videoframes=49, videosteps=20, videocfg=3.5, videoresolution=Image, videoformat=h264-mp4`. ⚠ the `videosteps/videocfg/videoresolution` trio is what makes the I2V step fire. To animate an *existing* still add `initimage=<base64>, initimagecreativity=0`. Output array has the first frame **and** the mp4. |
| **Image-edit** (Qwen-Image-Edit) | `model=qwen_image_edit_2511_fp8mixed.safetensors, initimage=<base64>, prompt="change the apple to a green apple", steps=20, cfgscale=2.5` |
| **In-image text** (Qwen-Image base GGUF — GATED tier) | opt-in by **selecting the model in the picker** (`-Model Qwen_Image-Q4_K_M.gguf` / Studio picker): `model=Qwen_Image-Q4_K_M.gguf, steps=20, cfgscale=4, sampler=euler, scheduler=simple` — the additive image-only family override (the strong **in-image text** base; reuses the Qwen2.5-VL TE + VAE from Qwen-Image-Edit). A gated `-Models full` download. ⚠ on-GPU: the GGUF arch detect + city96 GGUF node popup + exact step/cfg in the 20–50 / cfg 4 band + 32GB fit are the labeled remaining confirms. |
| **Upscale** (4×-UltraSharp) | add to any gen: `refinermethod=PostApply, refinercontrolpercentage=0, refinerupscale=2, refinerupscalemethod=model-4x-UltraSharp.pth`. ⚠ control 0 = pure upscale, no refine pass; it only fires when `refinermethod`+`refinercontrolpercentage` are both set. **-Refine** = a real hi-res-fix: same fields but `refinercontrolpercentage=0.35, refinerdotiling=true` so the upscale pass also regenerates coherent detail. |
| **Face refine** (-Face) | append ` <segment:face,0.4,0.5>` to the prompt: SwarmUI's CLIP-text Segment system masks the face and inpaint-refines it (the ADetailer equivalent — no extra model). `0.4` = creativity, `0.5` = match threshold. **image / edit / i2v** only. |
| **Realism LoRA** (-Realism) | append ` <lora:Z-Image-Realism:0.7>` to the prompt: applies a Z-Image realism LoRA (photoreal skin/detail) at weight 0.7. The `Z-Image-Realism.safetensors` must live in `Models\Lora` (fetched by `setup.ps1 -Models full`). **image / edit / i2v** only. |
| **Music** (ACE-Step 1.5 — turbo default) | `model=acestep_v1.5_turbo.safetensors, prompt="[instrumental]", textaudiostyle="upbeat electronic", textaudiobpm=128, textaudioduration=10, steps=10, cfgscale=1` → an mp3. **-Quality** swaps to the XL base (quality): `model=acestep_v1.5_xl_base_bf16.safetensors, steps=50, cfgscale=6, sampler=euler, scheduler=simple` (params from the official ComfyUI `audio_ace_step1_5_xl_base.json` example; bpm/duration unchanged). Turbo stays the music default; XL base is opt-in via -Quality. |
| **Video + synced SFX** (Foley) | `comfyuicustomworkflow=WanFoley, prompt=..., seed=-1` → one muxed mp4 with 48 kHz audio |
| **Face identity** (InstantID — GATED) | `comfyuicustomworkflow=InstantID, initimage=<base64 reference face>, prompt=...` → an identity-preserved SDXL gen (the reference face's identity carried onto a fresh image). Opt-in: install with `setup.ps1 -FaceId` (cubiq InstantID node + IP-Adapter/ControlNet/antelopev2 weights; SDXL, reuses the anime base — no new checkpoint). `doki gen -FaceId -InitImage <face.png>` is the ergonomic alias (the reference face rides the **init-image** channel, NOT SwarmUI's IP-Adapter-revision flag); `-FaceId` requires `-InitImage`. ⚠ on-GPU: the runnable `CustomWorkflows\InstantID.json` is the on-GPU authoring step (the upstream example is a ComfyUI UI-graph, not SwarmUI's API-prompt format) — until authored, `-FaceId` install wires node+weights only. |
| **Face identity** (PuLID-Flux — GATED) | `comfyuicustomworkflow=PuLID, initimage=<base64 reference face>, prompt=...` → an identity-preserved **FLUX.1-dev** gen (the reference face carried onto a fresh image). The FLUX sibling of the InstantID (SDXL) row. Opt-in: install with `setup.ps1 -Pulid` — clones the **balazik** PuLID-Flux node + fetches a **NON-GATED ~17GB FLUX fp8 base** (Kijai `flux1-dev-fp8` unet + `ae`, comfyanonymous `t5xxl`/`clip_l`) + `pulid_flux_v0.9.1`; **shares InstantID's antelopev2** (not re-downloaded if `-FaceId` ran). `doki gen -Pulid -InitImage <face.png>` is the ergonomic alias (the reference face rides the **init-image** channel); `-Pulid` requires `-InitImage`. ⚠ on-GPU: the node is **balazik Alpha V0.1.0 / last commit 2024-10-03** (stale — verify it LOADS on the current ComfyUI FIRST), and the runnable `CustomWorkflows\PuLID.json` is the on-GPU authoring step (balazik's example is a ComfyUI UI-graph, not SwarmUI's API-prompt format) — until authored, `-Pulid` install wires node+weights only. |
| **Talking-video** (InfiniteTalk — GATED, heavy) | `comfyuicustomworkflow=InfiniteTalk, initimage=<base64 portrait>, inputaudio=<base64 wav/mp3>, prompt=...` → an audio-driven talking-video (MeiGen InfiniteTalk on the **Wan2.1-I2V-14B** base, run via Kijai's WanVideoWrapper; the portrait lip-syncs to the voice). Opt-in: install with `setup.ps1 -InfiniteTalk` — clones `kijai/ComfyUI-WanVideoWrapper` + fetches the InfiniteTalk fp16 adapter + chinese-wav2vec2 + **an ~82GB Wan2.1-I2V-14B base NOT otherwise on disk** (DokiDex ships Wan 2.2, a different arch). `doki gen -InfiniteTalk -InitImage <portrait> -Audio <clip>` is the ergonomic alias; it requires **BOTH** `-InitImage` (portrait) **and** `-Audio` (voice). ⚠ on-GPU: the runnable `CustomWorkflows\InfiniteTalk.json` is the on-GPU authoring step (Kijai's example is a UI-graph, MeiGen's are CLI configs — neither is SwarmUI API-prompt), the **32GB fit is unconfirmed** (needs an fp8 base + block-swap; native fp16 14B OOMs), and the audio body-key (`inputaudio` is provisional) is pinned against the authored workflow — until then `-InfiniteTalk` install wires node+weights only. |
| **Lip-sync re-sync** (LatentSync — GATED, light) | `comfyuicustomworkflow=LatentSync, inputaudio=<base64 wav/mp3>, prompt=...` → audio-driven lip **re-sync** of an EXISTING clip (ByteDance LatentSync 1.5 via the `ShmuelRonen/ComfyUI-LatentSyncWrapper` node; the **LIGHTER** alternative to InfiniteTalk — ~9.8GB / 8GB VRAM vs ~82GB). **I/O divergence:** LatentSync edits an existing video's mouth to the audio, it does NOT generate from a portrait — so the source video rides the **workflow's own video-input channel** and it requires **`-Audio` only** (no portrait `-InitImage`; passing `-InitImage` is rejected loudly). Opt-in: install with `setup.ps1 -LatentSync` — clones the wrapper node + fetches the LatentSync-1.5 weights + the **required SD-VAE** (`stabilityai/sd-vae-ft-mse` into `checkpoints/vae/`). `doki gen -LatentSync -Audio <clip>` is the ergonomic alias. ⚠ on-GPU: the runnable `CustomWorkflows\LatentSync.json` is the on-GPU authoring step (the wrapper's examples are ComfyUI UI-graphs, not SwarmUI API-prompt), the video-input channel + the audio body-key (`inputaudio` is provisional) are pinned against the authored workflow — until then `-LatentSync` install wires node+weights only. |

**Simple prompts:** wrap a lazy idea in `<mpprompt:a cat on a skateboard>` and the always-on 3B
rewriter (`:8013`) expands it at generate time (MagicPrompt). The default image path is **Z-Image
Base** (non-distilled, ~35 steps, real CFG + negatives = the quality ceiling); **-Fast** swaps to
**Z-Image Turbo** (8 steps, CFG 1) for seconds-fast drafts.

**-Face / -Realism** are opt-in SwarmUI prompt *tags* that `doki gen` appends **after** the
`<mpprompt:…>` wrapper (SwarmUI processes `<segment:…>` / `<lora:…>` itself, independently of the
rewriter), so they compose with `-Raw` and with each other. Both are off by default and apply to
**image / edit / i2v** only (never music / video / foley).

## Speech

- **TTS** — `POST :8004/v1/audio/speech` `{ model:"chatterbox", input:"...", voice:"Emily.wav", response_format:"wav" }` (OpenAI-compatible; `/upload_reference` for zero-shot voice cloning). Uncensored, watermark stripped.
- **STT** — `POST :8005/v1/audio/transcriptions` multipart `file=<audio>` + `model=parakeet` → `{ "text": ... }`.

## Memory (MCP tools, available in Crush)

- `memory_save(content, tags)` — persist a fact/decision/gotcha (one per note).
- `memory_search(query, limit)` — full-text recall; use at the start of a task.
- `memory_recent(limit)` · `memory_delete(memory_id)`.

Seed/refresh the store with this project's facts: `python serving\memory-mcp\seed.py`.

## See also

`docs/wiki/12-benchmarks.md` (how fast each of these is) · `verify.ps1` (the live smoke for every call) ·
`docs/wiki/8-image-and-video.md` (the friendly walkthrough) · `docs/frontier-roadmap.md` (what's
blocked and why).
