# 8. Image & Video Generation

← [Quick Start](7-quick-start.md) · [Home](Home.md)

---

DokiCode also makes **pictures and short videos** from a text description — fully
local, no filter, on the same RTX 5090.

## The one rule first

Image/video models are huge, so they **can't share the GPU with the coding brain**.
It's a *mode you switch into*:

```powershell
.\doki.ps1 up media     # stops the LLM, starts the image/video server
```

Then open **http://127.0.0.1:7801**, type a prompt, hit **Generate**. Switch back to
coding with `.\doki.ps1 up` (or `up coexist`).

## What's under the hood

- **Tool:** [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) (an easy web UI) on
  the ComfyUI engine — installed and wired up **completely automatically** by
  `setup.ps1 -Media` (no clicking through an install wizard).
- **Image:** **Z-Image Turbo** — fast, photoreal, uncensored. A 1024×1024 image in a
  few seconds.
- **Video:** **Wan 2.1 1.3B** — a ~1.5‑second clip in ~25 seconds, reliably. A bigger
  **Wan 14B** is available via `setup.ps1 -Media -Models full` for higher quality, but
  it's minutes‑per‑clip and tight on 32 GB VRAM — so 1.3B is the daily driver.

## "No filter" — and the one real limit

Like the rest of DokiCode, this is **local and unfiltered**: SwarmUI/ComfyUI impose no
content filter and the models are uncensored. Your prompts and images/videos never
leave the machine. The only hard limits are the law — no CSAM, and no sexual imagery
of real, identifiable people without consent. (Same framing as
[Local vs. the Big Clouds](4-local-vs-the-big-clouds.md): that's not platform
censorship, it's just illegal.)

## Setup (one command)

```powershell
.\setup.ps1 -Media          # installs SwarmUI + ComfyUI + the reliable models
.\setup.ps1 -Media -Models full   # ...plus Wan-14B, Chroma, LTX-Video (quality, heavier)
.\doki.ps1 up media         # run it -> http://127.0.0.1:7801
```

---

← [Quick Start](7-quick-start.md) · [Home](Home.md)
