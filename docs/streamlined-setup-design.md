# Design — Streamlined native setup + unrestricted media generation

| | |
|---|---|
| **Status** | Approved 2026-06-13 — implementing |
| **Decision** | One-command native control plane (no Docker/WSL) + local image & video gen |

## Goal

Make the whole DokiCode stack **one-command to set up and run** on Windows, fully
reproducible, with **no Docker/WSL**, and add **local, unrestricted image + video
generation**. The only things launched by hand are the three the user excluded:
Crush (CLI), Chatbox (chat), llama.vscode (editor).

## Why native, not Docker

GPU inference in Docker on Windows requires the WSL2 backend — the exact layer the
[TDD](TDD.md) rejected for the native-CUDA performance path (265 tok/s). A native
control plane delivers the same "streamlined, isolated, one-command" goal without
the WSL2 layer or its perf cost. (User decision, 2026-06-13.)

## Control plane — `doki.ps1`

A `docker compose`-style dispatcher at the repo root:

| Command | Effect |
|---|---|
| `doki up [agent\|coexist\|media]` | start a profile detached, health-check, report |
| `doki down` | stop all managed services (PID files in `.run/`) |
| `doki status` | list services + health |
| `doki restart [profile]` / `doki logs <svc>` | convenience |

**Profiles** (encode the VRAM rules so you don't have to):

- `agent` (default) — llama-swap `:8080` (coder-fast/big, full 128k)
- `coexist` — llama-swap + FIM `:8012` (use coder-fast-lite; ~27.6 GB, editor + agent)
- `media` — SwarmUI `:7801` for image+video

**GPU modes are mutually exclusive** on 32 GB: `up media` stops the LLM servers
first; `up agent|coexist` stops media. Process lifecycle = PID files + logs under
the repo-local `.run/`, taskkill-tree on stop, per-service HTTP health probes.

## Media generation

**Tool: SwarmUI** (MIT, .NET) on the **ComfyUI** engine — a friendly form UI with the
full node graph underneath, **one tool for both image and video**, and **no content
filter**. .NET is already present (used by the eval sandbox); SwarmUI auto-provisions
its own ComfyUI + Python backend. Served on `:7801`, installed under repo `media/`.

**Image models** (open-weight, unfiltered):
- **Chroma** (uncensored, FLUX-based) and/or **FLUX.1-dev** — flagship realism
- an **SDXL** community checkpoint (Juggernaut XL / Illustrious) — variety

**Video models** (open-weight, unfiltered):
- **LTX-Video 13B** — default: fastest, fits 32 GB comfortably
- **Wan 2.7** / **HunyuanVideo 1.5** — quality leaders, run quantized (fp8/gguf) +
  offload; slower, tighter VRAM. Opt-in.

**Unrestricted:** local tooling imposes no filter and these models are unfiltered.
The only hard lines are legal, not platform censorship — **no CSAM; no sexual imagery
of real, identifiable people without consent.** Applies equally to image and video.

**Expectations:** image is fast (seconds). Video is heavy even on a 5090 — quantized
models, short clips (a few seconds), minutes per clip.

## Install flow — `setup.ps1`

Idempotent bootstrap (recommended scope = verify + deploy + guided, with selectable
model bundles):

1. Preflight: GPU/driver (≥570 for Blackwell), free disk.
2. Host tools via winget/uv: Crush, Chatbox, uv (search MCP). Pinned.
3. Deploy configs: `harness/crush.json` → `~/.config/crush/`, llama.vscode settings → VS Code.
4. Media (`-Media`): clone + build SwarmUI under `media/`; download models straight into
   `media/SwarmUI/Models/**` (scripted — no GUI wizard). Bundles selectable with sizes
   shown; lean default = LTX-Video + one image model; Wan/Hunyuan opt-in.
5. Verify LLM assets present (llama.cpp/llama-swap binaries, GGUFs) — guided fetch if missing.
6. Smoke checks (`test-toolcall.ps1`, media `/` probe).

**Reproducibility:** `versions.lock` pins every component + model (name, version, size).

## Out of scope (now)

Auto-download of the ~84 GB LLM models (assumed present on this machine); in-chat image
gen via Open WebUI (SwarmUI's UI is the surface — can add later).

## Build order

1. Control plane: `doki.ps1` + PID/log-aware `start-serving.ps1` / `start-fim.ps1` / `start-media.ps1` ← **this commit**
2. `setup.ps1 -Media`: install SwarmUI + models, deploy configs
3. Run it, verify image + a short video generate, iterate until smooth
4. Docs: wiki "Media generation" page + Quick Start `doki` commands
