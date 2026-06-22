# EchoMimicV3 — isolated talking-avatar capability

EchoMimicV3 (audio-driven talking-head / lip-sync) runs in a **self-contained, isolated
environment** at `media/echomimic-iso/` (gitignored). It is deliberately NOT part of the main
SwarmUI/ComfyUI media stack or the `doki gen` pipeline. This file is the committed record of how
it was built and how to run it (the env itself is not in git).

## Why isolated

Its de-facto ComfyUI wrapper (`smthemex/ComfyUI_EchoMimic`) requires **TensorFlow 2.15** +
`retina-face` in the ComfyUI venv. TensorFlow alongside the working stack's torch 2.8 / CUDA is
fragile (the node's own README warns about it), and the working ComfyUI drives all of DokiDex's
image/video generation. So EchoMimicV3 gets its own venv + minimal ComfyUI; the production media
stack is never touched. Verified after setup: the working ComfyUI still has Python 3.12 / torch
2.8.0 / **no TensorFlow**.

## Location & structure

```
media/echomimic-iso/
  venv/                      # uv venv, Python 3.10.6 (torch 2.11.0+cu128, tensorflow 2.15.0, retina-face 0.0.17)
  ComfyUI/                   # minimal ComfyUI clone (only the EchoMimic + VideoHelperSuite nodes)
    custom_nodes/ComfyUI_EchoMimic     # smthemex/ComfyUI_EchoMimic @ 3a36b00f
    custom_nodes/ComfyUI-VideoHelperSuite  # Kosinkadink @ 4ee72c06
    models/echo_mimic        # JUNCTION to the shared, already-downloaded weights (no re-download)
    output/                  # rendered mp4s land here
  run_echomimic_comfy.ps1    # launcher (starts ComfyUI on :8198)
  run_render_test.py         # API driver: portrait + audio + V3 -> short render
  requirements-frozen.txt    # full 190-pkg freeze for reproducibility
```

## How to run

The isolated ComfyUI uses the GPU directly and is **outside doki's mode management**, so free the
GPU first (the LLM/media groups are mutually exclusive with it on the one 32GB card):

```powershell
.\doki.ps1 down                                   # free the GPU (agent/media groups)
.\media\echomimic-iso\run_echomimic_comfy.ps1     # starts ComfyUI on http://127.0.0.1:8198
# then either: open :8198, load custom_nodes\ComfyUI_EchoMimic\example_workflows\echov3_Workflow.json,
#   set LoadImage=portrait, LoadAudio=speech.wav, Echo_LoadModel.version="V3", Queue Prompt
# or headless: media\echomimic-iso\venv\Scripts\python.exe media\echomimic-iso\run_render_test.py
.\doki.ps1 up agent                               # restore the daily driver when done
```

Output: `media/echomimic-iso/ComfyUI/output/*.mp4` (h264 video + AAC audio, muxed). Verified render:
384×384, 46 frames, ~14GB peak VRAM, ~32s. **Inputs needed:** a face portrait + a short speech wav
(generate the wav via the Chatterbox TTS at `:8004` when the agent group is up).

## What was fetched (official sources, SHA-256 verified)

- `umt5_xxl_fp8_e4m3fn_scaled.safetensors` (6.74GB) + `clip_vision_h.safetensors` (1.26GB) — from
  `Comfy-Org/Wan_2.1_ComfyUI_repackaged`. **Required:** the working stack's Wan `.pth` files are
  raw WanVideoWrapper format whose keys stock ComfyUI's `CLIPLoader`/`CLIPVisionLoader` don't read.
- EchoMimicV3 transformer + Wan2.1-Fun-1.3B-InP base + wav2vec2-base-960h — the pre-downloaded
  weights under the shared `echo_mimic/` tree (reached via junction).
- `retinaface.h5` auto-downloads on first run (face detector).

## Iso-only node patches (do not affect the working stack)

1. `ComfyUI_EchoMimic/EchoMimic_node.py` — `torchaudio.save(...FLAC)` → `soundfile.write(...WAV)`
   (torchaudio 2.11 routes through torchcodec, which needs FFmpeg shared DLLs absent here).
2. `echomimic_v3/infer.py` — the redundant root-level transformer override falls back to
   `transformer/diffusion_pytorch_model.safetensors`; `use_mmgp="None"` (mmgp not needed with VRAM free).
3. Two single-GPU no-op stubs for the node's optional xfuser `dist` package (never hit on the V3 path).

## Quality tuning

The smoke render was intentionally tiny (384², 6 steps) for speed — quality is low. For real output
bump width/height (e.g. 768×768), `length`, and steps; the README suggests `partial_video_length`
~65 at 12GB (higher with more VRAM). Optionally add the lightx2v LoRA for the ~10-step fast path.
