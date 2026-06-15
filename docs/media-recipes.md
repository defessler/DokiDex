# Media & Speech Recipes — exact local API calls

Every capability, with the precise (often non-obvious) request that actually works on this
setup. All verified live (`doki verify` 16/16). Media calls go to SwarmUI on `:7801`; speech to
the TTS/STT services; memory via the MCP tools.

> Switch the GPU first: **`.\doki.ps1 up media`** for image/video/audio, **`up agent`** for
> chat/speech. They're mutually exclusive on 32 GB.

## Image & video (SwarmUI `POST :7801/API/GenerateText2Image`)

Every call needs a session: `POST :7801/API/GetNewSession` → `{ "session_id": ... }`, then put
that `session_id` in the body. Output paths come back in `images[]`.

| Capability | Key body fields |
|---|---|
| **Image** (Z-Image Turbo) | `model=SwarmUI_Z-Image-Turbo-FP8Mix.safetensors, steps=8, cfgscale=1, width/height=1024` |
| **Text→video** (Wan 2.2 5B) | `model=wan2.2_ti2v_5B_fp16.safetensors, textvideoframes=49, steps=20, cfgscale=3.5, width=832, height=480, videofps=24, videoformat=h264-mp4` |
| **Fast video** (LTXV) | `model=ltxv-2b-0.9.8-distilled.safetensors, textvideoframes=97, steps=8, cfgscale=1, width=768, height=512, videofps=24` (T5 auto-downloads first run) |
| **Image→video** (animate a still) | `model=<any image model>` **+** `videomodel=wan2.2_ti2v_5B_fp16.safetensors, videoframes=25, videosteps=20, videocfg=3.5, videoresolution=Image, videoformat=h264-mp4`. ⚠ the `videosteps/videocfg/videoresolution` trio is what makes the I2V step fire. To animate an *existing* still add `initimage=<base64>, initimagecreativity=0`. Output array has the first frame **and** the mp4. |
| **Image-edit** (Qwen-Image-Edit) | `model=qwen_image_edit_2511_fp8mixed.safetensors, initimage=<base64>, prompt="change the apple to a green apple", steps=20, cfgscale=2.5` |
| **Upscale** (4×-UltraSharp) | add to any gen: `refinermethod=PostApply, refinercontrolpercentage=0, refinerupscale=2, refinerupscalemethod=model-4x-UltraSharp.pth`. ⚠ control 0 = pure upscale, no refine pass; it only fires when `refinermethod`+`refinercontrolpercentage` are both set. |
| **Music** (ACE-Step 1.5) | `model=acestep_v1.5_turbo.safetensors, prompt="[instrumental]", textaudiostyle="upbeat electronic", textaudiobpm=128, textaudioduration=10, steps=10, cfgscale=1` → an mp3 |
| **Video + synced SFX** (Foley) | `comfyuicustomworkflow=WanFoley, prompt=..., seed=-1` → one muxed mp4 with 48 kHz audio |

**Simple prompts:** wrap a lazy idea in `<mpprompt:a cat on a skateboard>` and the always-on 3B
rewriter (`:8013`) expands it at generate time (MagicPrompt). Quality default vs fast preset is
just `steps` (e.g. Z-Image Base 30–50 steps vs Turbo 8).

## Speech

- **TTS** — `POST :8004/v1/audio/speech` `{ model:"chatterbox", input:"...", voice:"Emily.wav", response_format:"wav" }` (OpenAI-compatible; `/upload_reference` for zero-shot voice cloning). Uncensored, watermark stripped.
- **STT** — `POST :8005/v1/audio/transcriptions` multipart `file=<audio>` + `model=parakeet` → `{ "text": ... }`.

## Memory (MCP tools, available in Crush)

- `memory_save(content, tags)` — persist a fact/decision/gotcha (one per note).
- `memory_search(query, limit)` — full-text recall; use at the start of a task.
- `memory_recent(limit)` · `memory_delete(memory_id)`.

Seed/refresh the store with this project's facts: `python serving\memory-mcp\seed.py`.

## See also

`docs/benchmarks.md` (how fast each of these is) · `verify.ps1` (the live smoke for every call) ·
`docs/wiki/8-image-and-video.md` (the friendly walkthrough) · `docs/frontier-roadmap.md` (what's
blocked and why).
