# 8. Image & Video Generation

← [Quick Start](7-quick-start.md) · [Home](Home.md)

---

DokiCode also makes **pictures, short videos (now with sound), and turns a lazy one-line
prompt into a cinematic one automatically** — fully local, no filter, on the same RTX 5090.

## The one rule first

Image/video models are huge, so they **can't share the GPU with the coding brain**.
It's a *mode you switch into*:

```powershell
.\doki.ps1 up media     # stops the LLM, starts the image/video server
```

Then open **http://127.0.0.1:7801**, type a prompt, hit **Generate**. Switch back to
coding with `.\doki.ps1 up` (or `up coexist`).

## Simple prompts, automatically (the magic)

You don't have to write fancy prompts. A tiny always-on **prompt rewriter** (a 3B LLM on its
own port `:8013`) expands a lazy phrase into the rich, cinematic prompt these models were trained
on. Wrap your idea in an `<mpprompt:...>` tag and it rewrites at generate time —

```
<mpprompt:a cat on a skateboard>
```

becomes *"A sleek black cat, eyes gleaming, perched on a gnarled skateboard, wheels barely
touching the cobblestone street as the sun casts long shadows…"* — every generation, zero effort.
(Powered by the **MagicPrompt** SwarmUI extension, wired to the local rewriter **automatically** by
`setup.ps1`. It's uncensored — the rewriter never refuses or swaps your subject.) The rewriter is
small enough to run **alongside** the image/video model, so switching to media mode starts it too.

## What's under the hood

- **Tool:** [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) on the ComfyUI engine —
  installed and wired up **completely automatically** by `setup.ps1 -Media` (no install wizard).
- **Image:** **Z-Image Turbo** — fast, photoreal, uncensored (1024² in a few seconds). With
  `-Models full` you also get **Z-Image Base** (a higher-detail "quality" preset, real CFG +
  negatives, ~1 min/image) and **Chroma** (a softer/filmic FLUX-derived style).
- **Video:** **Wan 2.2 TI2V-5B** is the quality default — an 832×480 clip in **~55 s**, reliably,
  and it leaves ~18 GB of VRAM free so the rewriter runs alongside it. **Wan 2.1 1.3B** stays as
  the always-reliable fast floor (~25 s). *(The Wan 2.2 **A14B** is downloaded too, but its
  dual-expert design overflows the 32 GB card even at modest settings — it's kept for a future
  GGUF/block-swap path, not the default. The single-model 5B is the sweet spot on this hardware.)*
- **Audio:** **HunyuanVideo-Foley** adds **synced sound** to a clip. Generate via the one-click
  **WanFoley** workflow and you get a single MP4 with 48 kHz audio matched to the motion.

## "No filter" — and the one real limit

Like the rest of DokiCode, this is **local and unfiltered**: SwarmUI/ComfyUI impose no
content filter and the models are uncensored. Your prompts and images/videos never
leave the machine. The only hard limits are the law — no CSAM, and no sexual imagery
of real, identifiable people without consent. (Same framing as
[Local vs. the Big Clouds](4-local-vs-the-big-clouds.md): that's not platform
censorship, it's just illegal.)

## Setup (one command)

```powershell
.\setup.ps1 -Media                # installs SwarmUI + ComfyUI + the reliable models
.\setup.ps1 -Media -Models full   # ...plus Wan 2.2 (5B + 14B), Z-Image Base, Chroma, Foley audio, and the rewriter
.\doki.ps1 up media               # run it -> http://127.0.0.1:7801  (the prompt rewriter starts too)
```

---

← [Quick Start](7-quick-start.md) · [Home](Home.md)
