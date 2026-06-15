# How DokiDex Works — the whole system, end to end

The authoritative, as-built walkthrough of the entire stack: what each piece is, how they fit,
how a request flows, and why it's built this way. (For the original design rationale see
[`TDD.md`](TDD.md); for the friendly ELI5 version see [`wiki/Home.md`](wiki/Home.md); for the exact
API call of every capability see [`media-recipes.md`](media-recipes.md).)

DokiDex is a **fully-local AI stack on one machine** — RTX 5090 (32 GB VRAM), 64 GB DDR5, Windows
11 **native** (no Docker, no WSL). It does agentic coding, chat, autocomplete, web search, speech
in and out, image/video/music generation and editing, and gives the coding agent persistent
memory — all uncensored, nothing leaving the box at runtime. One PowerShell script (`doki.ps1`)
runs the whole thing like docker-compose, without Docker.

---

## 1. The whole system at a glance

```
                 you ──► Crush (coder CLI) · Chatbox (chat) · llama.vscode (autocomplete)
                          │ OpenAI API            │ MCP (stdio)
                          ▼                        ▼
          ┌──────────────────────────┐   ┌──────────────────────────────┐
   LLM    │ llama-swap  :8080         │   │ MCP servers                  │
   group  │  hot-swaps one of:       │   │  · web-search (DuckDuckGo)    │
  (agent/ │  coder-fast (30B, GPU)   │   │  · memory (sqlite FTS5)       │
  coexist)│  coder-big  (120B, +RAM) │   └──────────────────────────────┘
          │  coder-fast-lite (64k)   │   ┌──────────────────────────────┐
          ├──────────────────────────┤   │ speech (own venvs, ~CPU/4GB) │
          │ FIM autocomplete :8012   │   │  · TTS  Chatterbox  :8004     │
          └──────────────────────────┘   │  · STT  Parakeet    :8005     │
          ┌──────────────────────────┐   └──────────────────────────────┘
   MEDIA  │ SwarmUI / ComfyUI :7801  │
   group  │  image · video · i2v ·   │   ┌──────────────────────────────┐
          │  edit · upscale · music  │   │ prompt-rewriter :8013         │
          │  · Foley (video+audio)   │   │  expands <mpprompt:…> via 3B  │
          └──────────────────────────┘   └──────────────────────────────┘
                          ▲
          ┌───────────────┴──────────────┐        ┌─────────────────────────┐
          │ doki.ps1 — the control plane  │◄───────│ DokiDex Control (WPF)  │
          │ up/down/status/verify/doctor… │ status │ live cards, GPU meter,  │
          │ $Services + $Profiles + GPU   │  json  │ mode switch, logs, tests │
          │ group-exclusion              │───────►│ (doki panel)            │
          └───────────────────────────────┘ shell  └─────────────────────────┘
                          │
                  RTX 5090 32 GB · 64 GB DDR5 · Windows 11 native
```

**The one rule that shapes everything:** 32 GB of VRAM can't hold the coding brain *and* the
image/video models at once. So services are split into two mutually-exclusive **GPU groups** —
`llm` and `media` — and `doki` switches between them. The `llm` group also has small always-on
riders (TTS ~4 GB, STT on CPU) that fit alongside the coder. Everything below follows from this.

---

## 2. The control plane — `doki.ps1`

`doki.ps1` is the heart: a docker-compose-style manager with **no Docker**. It's data-driven from
two ordered hashtables:

- **`$Services`** — the registry. Each entry has: a launch script (`serving\start-*.ps1`), a
  health URL, a **`group`** (`llm` or `media`), a port, a UI url, an estimated `vramGB`, and an
  optional `requires` path (so a service that isn't installed is skipped cleanly).
- **`$Profiles`** — named sets: `agent` = `[llama-swap, tts, stt]`, `coexist` = `[llama-swap, fim]`,
  `media` = `[media, prompt-rewriter]`.

**GPU group-exclusion** is the core logic: `up media` first stops every running `llm`-group
service, then starts the media profile; `up agent`/`coexist` does the reverse. A per-service
`doki start <svc>` respects the same invariant (it stops the opposing group first). This is what
keeps the 32 GB card from ever being double-booked.

Each service starts **detached** via its `start-*.ps1` (a `Start-Process` writing a PID + a log to
`.run\<name>.{pid,log,log.err}`), and `doki` waits on the health URL before reporting it up.

**Verbs:**

| Verb | What it does |
|---|---|
| `up [agent\|coexist\|media]` | start a profile (default agent); switches GPU groups as needed |
| `down` | stop everything, free the GPU |
| `status` / `status json` | human table / machine JSON (services + health + a `gpu` object from nvidia-smi) |
| `start\|stop\|restart <svc>` | per-service control (group-guarded) |
| `logs <svc>` | tail a service log |
| `verify` | full-stack live smoke test (every capability) — see §8 |
| `doctor` | one-shot environment + install diagnostics |
| `test` | unit suite — installer + status-json + memory + control panel (fast, no GPU) |
| `panel` | launch the WPF control panel |

`status json` is the **single source of truth** the control panel consumes (§7) — it merges the
service registry, live health probes, the loaded-model menu from llama-swap, and a GPU gauge into
one JSON document.

---

## 3. Inference layer (the LLM group)

**`llama.cpp` `llama-server`** (native Windows CUDA build, b9616 / CUDA 13.3) is the engine —
fastest single-user path, native tool-call templates (`--jinja`), prompt caching, and MoE CPU
offload. It sits behind **`llama-swap`** (`:8080`), a Go binary that exposes one
OpenAI-compatible endpoint and hot-swaps the right `llama-server` per requested model id:

| Model id | What | VRAM | Use |
|---|---|---|---|
| `coder-fast` | Qwen3-Coder-30B-A3B UD-Q4_K_XL, fully on GPU, 128k ctx | ~26 GB | the daily driver (~230–265 tok/s) |
| `coder-fast-lite` | same at 64k ctx | ~21 GB | so it fits *alongside* FIM autocomplete |
| `coder-big` | gpt-oss-120b MXFP4, experts offloaded to RAM (`--n-cpu-moe 22`) | ~27 GB + ~40 GB RAM | the heavy hitter (~27 tok/s) |

Load-bearing flags: `--jinja` (native tool-call templates — critical for agent reliability),
`--cache-reuse` (prompt cache so each agent turn doesn't re-prefill the whole conversation),
`--cache-type-k/v q8_0 --flash-attn` (KV quant to fit 128k ctx in budget), `--n-cpu-moe` (tune so
coder-big sits ~30 GB VRAM).

**FIM autocomplete** (`:8012`) is a small Qwen2.5-Coder-3B running `--embedding`-style `/infill`
for sub-200ms editor completions. It only runs in **coexist** mode, where the coder drops to
`coder-fast-lite` (64k) so both fit in 32 GB. **The prompt-rewriter** (`:8013`, a 3B on its own
port) is a media-group rider — see §5.

---

## 4. The harness (how you actually use the LLM)

Three clients, all pointed at the local endpoint:

- **Crush** — the agentic coder CLI (the bake-off winner: 91% on an 11-task golden suite vs Claw
  Code's 45%). Configured in `harness/crush.json` (deployed to `~/.config/crush`): the `local`
  provider → `:8080`, plus two **MCP servers**:
  - **web-search** — keyless DuckDuckGo MCP (`uvx duckduckgo-mcp-server`), no API key, no AI cloud.
  - **memory** — the persistent-memory server (§6).
- **Chatbox** — desktop chat app → `:8080/v1`.
- **llama.vscode** — editor autocomplete → the FIM server `:8012`.

The single highest-leverage artifact for local-model quality is **`AGENTS.md`** in each repo
(build/test commands, rules, "use the memory MCP") — DokiDex ships a filled-in root `AGENTS.md`
and a template (`harness/AGENTS.md`).

---

## 5. Media layer (the MEDIA group)

**SwarmUI** (on the **ComfyUI** engine, `:7801`) is installed and wired up 100% headlessly by
`setup.ps1 -Media`. It runs the `media` GPU group. One model is resident at a time (the 32 GB
rule), so SwarmUI **swaps** models per request — which is why switching *capabilities* costs a load
(see [benchmarks](benchmarks.md)). Everything is driven by `POST :7801/API/GenerateText2Image` with
a session from `GetNewSession`; the exact body for each is in [media-recipes.md](media-recipes.md).
SwarmUI also wears the on-brand **DokiGen Void** theme (`media-assets/SwarmUI-DokiGenTheme/`,
compiled in and set as the default by `setup.ps1 -Media`; per-browser overridable in
User Settings -> Theme).

| Capability | Model | How it's invoked (the non-obvious bits) |
|---|---|---|
| Image | Z-Image Turbo (+ Base, Chroma) | photoreal in seconds; 8 steps, cfg 1 |
| Text→video | Wan 2.2 **TI2V-5B** | the quality default (832×480 ≈ 26 s); the A14B 14B overflows 32 GB |
| Image→video | Wan 2.2 5B via **`videomodel`** | SwarmUI-native; the `videosteps`/`videocfg`/`videoresolution` trio is what fires the I2V step (no custom workflow) |
| Fast video | LTXV-2b distilled | near-real-time (97 frames ≈ 10.6 s) |
| Image-edit | Qwen-Image-Edit-2511 | native instruction edit + inpaint (model + init image + prompt) |
| Upscale | 4×-UltraSharp | the Refiner-Upscale group: `refinermethod=PostApply` + `refinercontrolpercentage=0` |
| Music | ACE-Step 1.5 | native audio model; `textaudiostyle`/`bpm`/`duration` → an mp3 |
| Video + SFX | HunyuanVideo-Foley | the `WanFoley` **custom workflow** muxes synced 48 kHz audio into one mp4 |

**Simple prompts, automatically:** the **prompt-rewriter** (`:8013`, a 3B `llama-server`) is the
media-group's secret weapon. Wrap a lazy idea in `<mpprompt:a cat on a skateboard>` and SwarmUI's
MagicPrompt extension calls the local rewriter to expand it into the rich cinematic prompt the
models were trained on — every generation, uncensored, zero effort.

**Custom workflows** (e.g. Foley) are ComfyUI API-format JSON in `media-assets/` copied into
SwarmUI's `CustomWorkflows/`, invoked via `comfyuicustomworkflow=Name` with `${prompt}`/`${seed}`
placeholders. (Hand-authoring these has a limit — SwarmUI can't inject a ref-image/audio into a
raw custom workflow without editor-generated metadata, which is why lip-sync (Wan-S2V) and LTX-2
audio are blocked; see [frontier-roadmap.md](frontier-roadmap.md).)

---

## 6. Speech & memory (the riders)

- **TTS** — Chatterbox (`:8004`, own cu128 venv) — uncensored (Perth watermark stripped),
  OpenAI `/v1/audio/speech` + zero-shot voice cloning. ~4 GB, so it rides **alongside** the coder
  in agent mode (no GPU switch).
- **STT** — Parakeet via onnx-asr (`:8005`, own venv) — OpenAI `/v1/audio/transcriptions`, CPU
  execution provider (no VRAM), also an agent-mode rider.
- **Memory** — `serving/memory-mcp` is a stdio MCP server (`memory_save`/`search`/`recent`/`delete`)
  backed by **sqlite + FTS5** full-text search, launched by Crush via `uv run --with mcp[cli]`. It
  gives the coding agent memory that survives across sessions; `seed.py` pre-loads it with this
  project's hard-won facts. No GPU, no model download.

---

## 7. The control panel — `doki panel`

A native **C# WPF (.NET 9)** app (`control/`) that's a thin, reactive face over the control plane.
It **reads one source of truth** — `doki status json`, polled every 2 s — and **shells `doki`** for
every action (it never re-implements the control logic). It shows: service cards grouped into
LLM/MEDIA bands (the idle band recessed so the 32 GB rule is *visible*), a GPU trust-meter, a mode
switcher with 32 GB-headroom math + an eviction-confirm sheet, live file-tailed logs, a
per-modality ⚡test, a coder model-swap, and update badges. It opens with a cinematic boot sequence,
wears a premium void/cyan/gold theme, and **self-updates** from its own GitHub releases (`Services/Updater.cs`,
applied in place on launch — distinct from the upstream SwarmUI/llama-swap badges). It has **41 unit
tests** (`doki test`) on its parsing + state + auto-updater logic. (Design: [control-panel-design.md](control-panel-design.md).)

---

## 8. Verification & ops

- **`doki verify`** — the trust anchor. Cycles agent → coexist → media and hits **every** capability
  with a real API call (chat, autocomplete, TTS, STT, memory, image, three video paths, image-to-
  video, image-edit, music, upscale, Foley audio) — 15 live smokes, each skipping cleanly if its
  asset isn't installed. Restores agent mode at the end. This is run before any capability is
  called "done."
- **`doki doctor`** — environment + install diagnostics: GPU/driver/VRAM, disk, the toolchain,
  model inventory (multi-part-aware), the media kit, per-service installable+port state, memory +
  panel status.
- **`doki test`** — the fast no-GPU unit suite (installer helpers + status-json contract + memory store + control panel).

---

## 9. Why it's built this way

- **One box, fully local, uncensored.** No cloud AI at runtime; prompts/outputs never leave the
  machine. "Uncensored" = the local tools impose no content filter; the only limits are legal.
- **Native Windows, no Docker/WSL.** Fastest single-user path on this hardware; avoids the
  ~20–40% WSL penalty and container overhead. The cost is PowerShell-everywhere and watching
  Blackwell (sm_120) wheel availability.
- **GPU group-exclusion over a smaller model.** Rather than shrink the coder to coexist with media,
  the stack keeps both at full quality and *switches* — a one-command, well-signposted operation.
- **Eval-gated.** Every model choice went through a measured bake-off (see
  [decisions.md](decisions.md) / [benchmarks.md](benchmarks.md)); nothing is adopted on vibes.
- **Data-driven control plane.** New services are one `$Services` entry — the panel, `status json`,
  and `doctor` all pick them up with no extra code.

---

## 10. Hard-won lessons (the gotchas)

These cost real debugging and are seeded into the memory store:

- **Blackwell sm_120:** no flash-attn wheel on native Windows — use SDPA. Watch protobuf/onnx pins.
- **Wan 2.2 5B uses `wan2.2_vae`**, not `wan_2.1_vae` (that's the 1.3B floor's).
- **Image-to-video is native** (`videomodel` + the steps/cfg/resolution trio), *not* a custom
  workflow — the custom-workflow image-injection path is blocked for hand-authored graphs.
- **Upscale** only fires via the Refiner group with `refinercontrolpercentage=0`.
- **STT:** a Form param named `model` shadows the module-level `model()` loader — alias it.
- **32 GB ceiling:** Wan 2.2 A14B and LTX-2 (19B + a 12B Gemma encoder) don't fit; the 5B is the
  practical video max. The audio-video frontier (S2V/SUPIR/LTX-2) is mapped and blocked.

---

## 11. Where everything lives

| Path | What |
|---|---|
| `doki.ps1` | the control plane (this whole doc is mostly about this file) |
| `setup.ps1` | one-command headless bootstrap (prereqs, configs, models) |
| `verify.ps1` | the full-stack live smoke test |
| `serving/` | `start-*.ps1` launch scripts, `llama-swap.yaml`, `memory-mcp/`, `stt-server.py` |
| `harness/` | `crush.json`, `AGENTS.md` template, editor/chat configs |
| `media-assets/` | the committed ComfyUI custom workflows (e.g. `WanFoley.json`) |
| `control/` | the WPF control panel + its `DokiDex.Control.Tests` |
| `docs/` | this doc, `TDD.md`, `media-recipes.md`, `benchmarks.md`, `decisions.md`, `frontier-roadmap.md`, the `wiki/` |
| `models/` · `media/` · `tts/` · `stt/` | model weights & local installs — **git-ignored** |

---

*Everything here is verified live (`doki verify`) and measured ([benchmarks.md](benchmarks.md)).
This document reflects the as-built system on 2026-06-14.*
