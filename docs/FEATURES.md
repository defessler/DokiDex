# DokiDex — Feature Index

**DokiDex is a fully-local AI stack on one machine** — RTX 5090 (32 GB VRAM), 64 GB DDR5, Windows 11 **native** (no Docker, no WSL). It does agentic coding, chat, autocomplete, web search, persistent agent memory, speech in/out, and image/video/music/audio generation + editing — **all uncensored, with nothing leaving the box at runtime** (the only network egress is keyless web search). One PowerShell script (`doki.ps1`) runs the whole thing like docker-compose, without Docker. This is the scannable feature index; for the full architecture see [`docs/how-it-works.md`](docs/how-it-works.md), and for exact API calls see [`docs/media-recipes.md`](docs/media-recipes.md).

> **The one rule that shapes everything:** 32 GB of VRAM can't hold the coding brain *and* the image/video models at once, so services split into two mutually-exclusive **GPU groups** — `llm` and `media` — and `doki` switches between them. The `llm` group has small always-on riders (TTS ~4 GB, STT on CPU) that fit alongside the coder. This single constraint is enforced on every start and is why most of the control plane exists.

---

## Coding & chat

The inference layer is llama.cpp `llama-server` (native Windows CUDA) behind **llama-swap** — one OpenAI-compatible endpoint on `:8080` that hot-swaps the right model per request.

| Feature | What it does | How to use |
|---|---|---|
| **llama-swap endpoint** `:8080` | Single OpenAI-compatible endpoint that hot-swaps the right `llama-server` per requested model name (llama.cpp b9616 CUDA 13.3, llama-swap v224). | Base `http://127.0.0.1:8080/v1/`; `POST /v1/chat/completions` with `model` = `coder-fast`\|`coder-big`\|`coder-fast-lite`. Health: `GET /v1/models`. |
| **coder-fast** — Qwen3-Coder-30B-A3B | Daily-driver coder MoE fully on GPU; 30B-A3B (UD-Q4_K_XL) @ 131072 ctx, ~26 GB VRAM, **265 tok/s**. | `model: coder-fast`. Default for both Crush roles (`large` and `small`). |
| **coder-big** — gpt-oss-120b | Heavy-hitter ~120B sparse MoE (5.1B active) with the first-24-layer experts CPU-offloaded; GPU ~27 GB + RAM ~40 GB, 131072 ctx. Carries extra `--n-cpu-moe 22 -b 2048 -ub 2048`. | `model: coder-big`. Tune `--n-cpu-moe` down (more experts on GPU = faster) until VRAM ~30 GB. First load pages ~60 GB from disk (900 s health timeout). |
| **coder-fast-lite** — Qwen3-Coder-30B @64k | Same 30B weights at 65536 ctx (~21 GB) so it coexists with FIM autocomplete in 32 GB (full-128k `coder-fast` cannot). | `model: coder-fast-lite`. Use in the `coexist` profile alongside `llama.vscode`. |
| **Crush coder CLI** | Daily-driver agentic coding CLI (bake-off winner, v0.76, **91%** on the 11-task golden suite) wired to llama-swap with native tool calls. | Launch `crush` yourself. Config deployed to `~/.config/crush/crush.json` from `harness/crush.json` (provider `local`, base_url `:8080/v1/`, key `dummy`). |
| **OpenCode challenger config** | Shipped alternate harness config for the bake-off runner-up (provider `local`, `coder-fast`/`coder-big`). Crush won; this remains for re-running the comparison. | `harness/opencode.json`. |
| **Chatbox chat app** | Desktop chat client for conversational use (GUI, no CLI command). | Launch Chatbox yourself. One-time GUI step (called out by `setup.ps1`): Settings → add an OpenAI-compatible provider, API Host `http://127.0.0.1:8080/v1`, key `dummy`, models `coder-fast` / `coder-big`. |
| **llama-swap web UI** | Browser UI for the inference endpoint. | `http://127.0.0.1:8080/ui` |
| **Model introspection** | Reports which model is loaded + which are configured; consumed by `doki status json` and the panel's model-swap chips. | `GET /running`, `GET /v1/models`. |

*All three coder models share `--flash-attn on`, `q8_0` K/V-cache quantization (`--cache-type-k/-v q8_0`), `--cache-reuse 256`, `-ngl 99`, and `--jinja` (native tool calls); `default_max_tokens` is 8192 each. `coder-big` additionally adds the batch/offload flags noted above.*

---

## Autocomplete

| Feature | What it does | How to use |
|---|---|---|
| **FIM infill server** `:8012` *(optional)* | Standalone fill-in-the-middle autocomplete; Qwen2.5-Coder-3B Q8 at 16384 ctx, ~4–6.6 GB VRAM. Independent of llama-swap so the editor and the agent can run at once. | `.\doki.ps1 up coexist`. Health `GET :8012/health`; infill `POST :8012/infill`. |
| **llama.vscode editor autocomplete** *(optional)* | VS Code extension giving live inline FIM completions, with editor-chat pointed at llama-swap. | Launch llama.vscode yourself; run `up coexist`; point the coder at `coder-fast-lite` so both fit in 32 GB. Settings shipped as the partial `harness/llama.vscode-settings.json` (FIM → `:8012`, chat → `:8080/v1`, RAG **off**, Copilot disabled) — `setup.ps1` prompts you to merge it into `Code/User/settings.json`. |

---

## Web search

| Feature | What it does | How to use |
|---|---|---|
| **Keyless DuckDuckGo web-search MCP** | Web search over MCP — no API key, no AI-cloud traffic; the **only** allowed network egress. | Defined in `crush.json` → `mcp.websearch` (`uvx duckduckgo-mcp-server`). Auto-launched by Crush when it uses the websearch tool. |

---

## Persistent memory

A local FastMCP server (`doki-memory`, over stdio) gives the coding agent memory that survives across sessions, backed by SQLite FTS5. The protocol lives in the repo's filled-in root `AGENTS.md`: `memory_search` before a non-trivial task, `memory_save` on decisions/gotchas.

| Tool / feature | What it does | How to use |
|---|---|---|
| `memory_save` | Save a fact/decision/preference with optional comma-separated tags; returns the new id. | `memory_save(content, tags="")` |
| `memory_search` | Full-text keyword search over stored memories (returns id, tags, content). | `memory_search(query, limit=5)` |
| `memory_recent` | List the most recently saved memories for a quick catch-up. | `memory_recent(limit=10)` |
| `memory_delete` | Delete a memory by id when a fact goes stale. | `memory_delete(memory_id)` |
| **FTS5 backend + LIKE fallback** | Relevance-ranked FTS5 (`mem_fts` synced via triggers); degrades gracefully to a `LIKE` scan if FTS5 is unavailable. | Automatic. |
| **Auto-created schema** | Creates the `memories` table, FTS virtual table, and sync triggers on every connection. | Automatic. |
| **Seed script** | Idempotently loads **14** hard-won DokiDex facts/gotchas (clears prior `seed` notes first) so the agent starts with real project knowledge. | `python serving/memory-mcp/seed.py` |
| **Config** | DB at `serving/memory-mcp/memory.db`; path overridable via `MEMORY_DB`. Wired in `crush.json` → `mcp.memory` (`uv run --with mcp[cli] server.py`). | Automatic via Crush. |

---

## Speech — TTS

Uncensored, fully-local text-to-speech (Chatterbox by devnen/Resemble) on `:8004`, in its own cu128 venv. Only ~4 GB VRAM, so it's in the `llm` group and **coexists** with the coder (no GPU-exclusive mode); started by `up agent`.

| Feature | What it does | How to use |
|---|---|---|
| **Chatterbox TTS server** `:8004` | The speech engine; part of the `agent` profile. | `.\doki.ps1 up` (or `up agent`), or `.\serving\start-tts.ps1` foreground. UI at `http://127.0.0.1:8004/`. First start downloads the voice model. |
| **OpenAI `/v1/audio/speech`** | Drop-in OpenAI audio-speech API surface. | `POST :8004/v1/audio/speech` |
| **Zero-shot voice cloning** | Clone a voice from a reference sample — no per-voice training. | Upload a reference, then synthesize with that voice. |
| **Perth watermark stripped** | Output audio is genuinely unmarked (watermark removed in every chatterbox model file at install; uses the public `chatterbox` repo, not the gated turbo one). | Automatic (done at install). |

---

## Speech — STT

Fully-local speech-to-text (NVIDIA Parakeet TDT 0.6B v2 via onnx-asr) on `:8005`, FastAPI/uvicorn, own venv (~300 MB). Runs on **CPU by default** (~no VRAM), so it's in the `llm` group and coexists with the coder in `agent` mode; started by `up agent`.

| Feature | What it does | How to use |
|---|---|---|
| **Parakeet STT server** `:8005` | The transcription engine; part of the `agent` profile. | `.\doki.ps1 up` (or `up agent`), or `.\serving\start-stt.ps1` foreground. |
| **OpenAI `/v1/audio/transcriptions`** | Drop-in OpenAI transcription API; **verbatim, no content filter**. | `POST :8005/v1/audio/transcriptions` (multipart `file`, optional `model`) → `{"text": …}`. |
| **Status / health endpoints** | `/` reports model id + ONNX providers; `/health` is a liveness probe. | `GET :8005/` · `GET :8005/health` |
| **Lazy model load + cache** | Downloads the Parakeet ONNX model from HF on first call (~2 GB), then caches it. | Automatic on first transcription. |
| **GPU acceleration** *(optional)* | Switch to the CUDA execution provider. | Set env `STT_PROVIDER=cuda`. |
| **Configurable model** *(optional)* | Override the onnx-asr model id. | Set env `STT_MODEL=<id>` (default `nemo-parakeet-tdt-0.6b-v2`). |

---

## Media stack — orchestration & API

All media gen runs through **SwarmUI** (front-door API/UI) with a **ComfyUI** backend, both installed 100% headlessly. Image generation is verified live; most quality tiers are **optional** (full-tier, installed with `-Models full`).

| Feature | What it does | How to use |
|---|---|---|
| **SwarmUI / ComfyUI server** `:7801` | The media host; serves image/video/audio gen with a web UI. ComfyUI backend installed headlessly via the `InstallConfirmWS` WebSocket (no GUI wizard). | Installed by `.\setup.ps1 -Media`; run via `.\doki.ps1 up media`. UI at `http://127.0.0.1:7801/`. |
| **Session + generation endpoints** | Every gen needs a session first; output paths return in `images[]`. | `POST /API/GetNewSession` → `{session_id}`; then `POST /API/GenerateText2Image` with `session_id` + params. |

---

## Image generation

| Model | What it does | How to use |
|---|---|---|
| **Z-Image Turbo** *(default)* | Uncensored, fast, photoreal text-to-image (~seconds/image, 1024²); the verified reliable default. | `model='SwarmUI_Z-Image-Turbo-FP8Mix.safetensors'`, `steps=8`, `cfgscale=1`, 1024². Always installed with `-Media`. |
| **Z-Image Base** *(optional)* | Non-distilled Z-Image for the quality ceiling — quality default is just more steps. Reuses Turbo's auto-fetched encoder/VAE. | `model='z_image_bf16.safetensors'`, 30–50 steps. `-Models full`. |
| **Chroma1-HD** *(optional)* | Uncensored, FLUX-derived stylized image model. | `model='Chroma1-HD-fp8mixed-final.safetensors'` (use the `-final` stable variant). `-Models full`. |

---

## Image editing

| Feature | What it does | How to use |
|---|---|---|
| **Qwen-Image-Edit-2511** *(optional)* | SwarmUI-native instruction-based image edit + free inpaint (e.g. "change the apple to a green apple"). UI: "Qwen Image Edit Plus". fp8mixed (~20 GB). | `model='qwen_image_edit_2511_fp8mixed.safetensors'`, `initimage=<base64>`, `prompt='<instruction>'`, `steps=20`, `cfgscale=2.5`. `-Models full`. |

---

## Upscaling

| Feature | What it does | How to use |
|---|---|---|
| **4x-UltraSharp** *(optional)* | 4× ESRGAN upscaler exposed as SwarmUI's Refiner/Upscale step (stills + video frames). | Add `refinermethod='PostApply'`, `refinercontrolpercentage=0` (control 0 = upscale, no refine), `refinerupscale=2`, `refinerupscalemethod='model-4x-UltraSharp.pth'`. `-Models full`. |

---

## Video generation

The reliable default is **Wan 2.1 1.3B** (always installed); the recommended quality tier that fits 32 GB is **Wan 2.2 TI2V-5B**. All `-Models full` items are **optional**.

| Model | What it does | How to use |
|---|---|---|
| **Wan 2.1 1.3B** *(default/floor)* | Uncensored text-to-video; reliable default that fits 32 GB with headroom (~25 s/clip). | `model='wan2.1_t2v_1.3B_fp16.safetensors'`, `textvideoframes=17`, `steps=20`, `cfgscale=6`, 480×320, `videofps=16`, `videoformat='h264-mp4'`. Always installed with `-Media`. |
| **Wan 2.2 TI2V-5B** *(quality)* *(optional)* | Higher-quality text-to-video that reliably fits 32 GB; the recommended quality tier (832×480 in ~55 s). fp16 only. | `model='wan2.2_ti2v_5B_fp16.safetensors'`, `textvideoframes=49`, `steps=20`, `cfgscale=3.5`, 832×480, `videofps=24`. `-Models full`. |
| **Image-to-video (Wan 2.2 5B animator)** *(optional)* | Animate a still (generated or supplied) into a clip via SwarmUI's native videomodel pipeline; output has the frame-1 image **and** the mp4. | On `/API/GenerateText2Image`: `model=<image model>` + `videomodel='wan2.2_ti2v_5B_fp16.safetensors'`, `videoframes=25`, plus the **required** `videosteps=20`, `videocfg=3.5`, `videoresolution='Image'`. Animate an existing still: add `initimage=<base64>`, `initimagecreativity=0`. `-Models full`. |
| **LTXV-2b-0.9.8-distilled** *(fast)* *(optional)* | SwarmUI-native near-real-time video, long clips up to ~257 frames; a speed option below Wan 2.2 (~97 frames 768×512 in ~36 s incl. T5 auto-download). | `model='ltxv-2b-0.9.8-distilled.safetensors'`, `textvideoframes=97`, `steps=8`, `cfgscale=1`, 768×512. `-Models full`. |
| **Wan 2.2 14B A14B MoE** *(optional, ⚠ exceeds 32 GB)* | Highest-quality Wan T2V/I2V 14B dual-expert (high+low noise, one expert GPU-resident per phase via StepSwap). **Downloaded but intentionally not the default — exceeds 32 GB VRAM; no dedicated smoke test.** | T2V/I2V high+low fp8_scaled in `diffusion_models`. `-Models full`. |
| **Wan2.2-Lightning 4-step LoRAs** *(optional)* | 4-step distillation LoRAs (high+low) — the "fast" preset for the Wan 2.2 14B models. | Applied as LoRAs on a Wan 2.2 14B gen (saved as `Wan22-Lightning-{T2V,I2V}-{HIGH,LOW}`). `-Models full`. |

---

## Music & audio

| Feature | What it does | How to use |
|---|---|---|
| **ACE-Step 1.5 music** *(optional)* | SwarmUI-native audio model (class `ace-step-1_5`) — generates instrumental clips as 48 kHz stereo MP3. Ships an XL base (max quality) and a turbo (fast) variant; qwen ace15 text-encoders auto-download. | `model='acestep_v1.5_turbo.safetensors'`, `prompt='[instrumental]'`, `textaudiostyle=…`, `textaudiobpm=128`, `textaudioduration=10`, `steps=10`, `cfgscale=1`. `-Models full`. |
| **Wan → HunyuanVideo-Foley** *(optional)* | Custom ComfyUI workflow: a Wan 2.2 5B clip **with synced Foley sound**, returned as one muxed mp4 (48 kHz audio). CLAP + SigLIP2 encoders auto-download on first run. License: Tencent Hunyuan Community (local/personal use). | `comfyuicustomworkflow='WanFoley'`, `prompt=…`, `seed=-1` → one muxed mp4. Installs the Foley node + models. `-Models full`. |

---

## Simple-prompt rewriting

| Feature | What it does | How to use |
|---|---|---|
| **`<mpprompt:…>` prompt-rewriter (MagicPrompt)** *(optional)* | Always-on local 3B LLM (`:8013`, Qwen2.5-3B Q5, ~2.5 GB at 8192 ctx) that expands a lazy idea into one rich cinematic prompt at generate time, in-line. Lives in the `media` group (peak ~24 GB with the active video expert), so it coexists with media gen, not the big coder. | Wrap any idea: `<mpprompt:a cat on a skateboard>` in a prompt on any gen. Served as model `prompt-rewriter`; auto-configured headlessly via `POST /API/SaveMagicPromptSettings`. `-Models full`. |

---

## Control plane — `doki.ps1`

A docker-compose-style manager with **no Docker**, data-driven from a `$Services` registry and `$Profiles` map. Enforces the `llm` ↔ `media` GPU mutual-exclusion on every start.

| Command | What it does |
|---|---|
| `doki up [agent\|coexist\|media]` | Start a profile detached (default `agent`); stops the opposite GPU group first, then waits on each service's health. **Profiles:** `agent` = llama-swap + tts + stt · `coexist` = llama-swap + fim · `media` = media + prompt-rewriter. Skips services whose `requires` path is absent. |
| `doki down` | Stop every managed service via its tracked PID file (taskkill `/T /F`). |
| `doki status` | Human-readable service + health table (UP healthy / UP starting / down). |
| `doki status json` | Machine-readable `{ services, profiles, gpu }` incl. loaded model, configured models, per-service install state, and the GPU gauge (used/total MB, util, temp, watts, fan, activeGroup). Feeds the panel. |
| `doki restart [profile\|service]` | Restart one named service, or down+up a whole profile. |
| `doki start <service>` / `stop <service>` | Per-service control, honoring the GPU group exclusion. Services: `llama-swap`, `fim`, `tts`, `stt`, `media`, `prompt-rewriter`. |
| `doki logs <service>` | Tail (`-Tail 40 -Wait`) a service's `.run/<name>.log`. |
| `doki verify` | Full-stack live smoke test (delegates to `verify.ps1`). |
| `doki doctor` | Environment + install diagnostics. |
| `doki test` | Fast no-GPU unit suite — installer helpers + `status json` contract + memory store + control panel (incl. updater). |
| `doki panel` | Launch the WPF control panel. |

**Lifecycle internals:** PID-file process tracking · untracked-instance detection (reports already-up if a service was started outside doki but its health responds) · HTTP health probe + wait-for-health (≤120 s) · forced PID-tree kill on stop · auto-created `.run/` state dir for `.pid`/`.log` files.

**GPU model:** group mutual-exclusion (`llm` vs `media`) enforced via each service's `group` field · per-service `vramGB` budgeting metadata (llama-swap 26, fim 4, tts 4, stt 1, media 18, prompt-rewriter 3) · GPU telemetry JSON from one `nvidia-smi` call · `activeGroup` detection · install-state detection via the `requires` guard.

**Shared launch contract:** every `serving/start-*.ps1` (serving, fim, media, prompt-rewriter, tts, stt) accepts `[-Detach] [-PidFile <p>] [-LogFile <l>]` to run hidden in the background with PID + log files — this is how `doki` launches and tracks each service.

---

## Control panel (WPF cockpit) *(optional)*

A single-process **WPF cockpit on .NET 9** (premium void/cyan/gold theme; native splash + a cinematic boot sequence) — a reactive face over `doki status json` that never re-implements control logic. Launch via `control.bat` (double-click; first run builds Release **and** creates a console-free `DokiDex.lnk` launcher with the arc-reactor icon), the `DokiDex.lnk` shortcut thereafter, or `doki panel`.

| Area | What it does |
|---|---|
| **Shell & polling** | Repo-root auto-discovery; 2-second background `doki status json` poll (skips overlapping ticks; "doki status unavailable" on null); three-zone shell (left rail / content / status strip) with Dashboard ↔ Logs nav. |
| **Service cards** | Data-driven (zero UI per new service); grouped LLM (cyan) / MEDIA (gold) bands with the inactive band recessed; state dot+label+detail (healthy/degraded/down/not-installed); degraded "starting" pulse; crashed red edge; ghost cards + setup hint for uninstalled services; per-service port + version/update badge. |
| **Per-service control** | Start / Stop / Restart (group-guarded by doki, gated on Installed) · Open web UI (when a `ui` URL exists). |
| **Coder model-swap chips** | One chip per configured llama-swap model; click warm-loads it via a 1-token chat request so llama-swap hot-swaps (`coder-fast`/`coder-big`/`coder-fast-lite`). |
| **Per-modality ⚡ test** | One-click smallest real gen per modality into a result tray with ✓/✕ + elapsed ms (disabled unless healthy). Covers chat (`:8080`), FIM infill (`:8012`), prompt-rewriter (`:8013`), TTS (`:8004`), and image (`:7801`). |
| **Mode switcher** | Pick-one AGENT / COEXIST / MEDIA segmented buttons; active-mode derived from running services; hover-preview explainer; predictive 32 GB-headroom readout (`~N GB · ~(32-N) GB free` or `⚠ exceeds 32 GB`); eviction-confirm sheet with WILL STOP / WILL START columns (Switch disabled if it doesn't fit). |
| **GPU trust-meter** | Stacked 32 GB bar attributed to the active group; used/total GB, %, free, temp, watts, fan; low-headroom (<2 GB) + hot-temp (≥80°C) warnings; "GPU n/a" fallback when `nvidia-smi` is unavailable. *(Per-process VRAM is N/A on WDDM — honest aggregate.)* |
| **Live logs** | Tails `.run/<name>.log[.err]` by byte-offset (1 s, handles rotation, caps 2500 lines, auto-scroll); per-service tabs + an "All" merge; regex/substring filter; pause toggle; stderr toggle; **content-based** severity coloring (not stream-based). |
| **Global actions** | Check Updates (SwarmUI via git, llama-swap via GitHub release → card badges) · Verify stack (visible console) · Stop All. |
| **Cinematic boot + native splash** | A native `<SplashScreen>` PNG shows instantly, cross-fading into an animated **"THE SEAL IGNITES"** boot — a gold FF summoning hexagram = Iron Man arc-reactor faceplate → fires a cyan packet to a Star Trek LCARS rail populated from real `doki status json`. Skippable (key/click), reduced-motion aware; a curtain timer always opens the panel even if every probe fails. (`Views/BootWindow.xaml.cs`, shown first by `App.xaml.cs`.) |
| **In-app self-updater** | Checks the panel's OWN GitHub releases (`defessler/DokiDex`); shows a "panel update available" banner, then downloads + PE-verifies + swaps the exe **in place** (copy-beside → same-volume rename-and-relaunch, no admin/installer), and auto-applies a staged update at next launch. Gated so it never runs under `dotnet run`. Distinct from the per-service "Check Updates" above. (`Services/Updater.cs`.) |
| **Arc-reactor icon + `DokiDex.lnk`** | First-run `control.bat` / `doki panel` generates the multi-res arc-reactor app icon (`make-icon.ps1` → `assets/dokidex.ico`) and a console-free `DokiDex.lnk` shortcut to the WinExe (`make-shortcut.ps1`) — the day-to-day entry point. |
| **Theme & tests** | Premium void/cyan/gold theme (`Themes/Palette.xaml`) + value converters; **21 xUnit test methods** across 5 classes — status parsing (3), GPU view-model (3), service view-model (6), log classification (2), updater (7) — which expand to **41 cases** via `InlineData` (`dotnet test control\DokiDex.Control.Tests`). |

---

## Verification & ops

| Feature | What it does | How to use |
|---|---|---|
| **`doki verify`** — full-stack smoke test | Cycles GPU modes (agent → coexist → media), runs live capability smokes with a real API call each, prints a PASS/SKIP/FAIL table, restores `agent` (the default resting state), and exits 0 only if zero failures. **15 result rows: 5 always run, 10 SKIP cleanly when a full-tier model is absent.** | `.\doki.ps1 verify` |
| **`doki doctor`** — diagnostics | ok/warn/miss marks across: GPU hardware (nvidia-smi) · disk free (warn <20 GB) · toolchain (pwsh/dotnet/git required; python/uv/gh/crush/ffprobe optional) · LLM model inventory · media kit (lean + full) · per-service install+port · memory store · control-panel build. | `.\doki.ps1 doctor` |
| **`doki test`** — unit tests | Runs the fast no-GPU suite: installer-helper + `status json`-contract PowerShell tests (AST-extracted from the real scripts), the sqlite/FTS5 memory tests, and the control-panel xUnit incl. the auto-updater (~118 assertions total). | `.\doki.ps1 test` |
| **Releases & auto-update** | Push a `v*` tag (`git tag v0.2.0 && git push origin v0.2.0`) → `.github/workflows/release.yml` builds a self-contained single-file `DokiDex-v0.2.0-win-x64.exe`, embeds the version, and publishes it as a GitHub release — the payload the panel's in-app updater downloads and swaps in place. `workflow_dispatch` builds/validates only. The exe must live inside a cloned repo (it shells `doki.ps1`). | `git tag v* && git push --tags` |

**Always-run smokes (5):** chat/code (`:8080`, real completion), memory MCP (exercises the `memory_db` store/search core directly on a temp DB — the stdio MCP wrapper itself is exercised by Crush, not verify), autocomplete/FIM infill (`:8012`), image (Z-Image Turbo), video (Wan 2.1 1.3B).

**SKIP-gated smokes (10, run only if the asset is installed):** TTS (`:8004`); **STT round-trip (`:8005`)** — transcribes the clip the TTS smoke generated, so it SKIPs with *"no audio sample; needs -Tts above"* if TTS isn't installed first; prompt-rewriter (`:8013`); Wan 2.2 5B; image-to-video; Qwen image-edit; ACE-Step music; 4x-UltraSharp upscale; LTXV fast video; and Wan→Foley video-with-audio (audio stream confirmed via ffprobe when present).

---

## Setup — `setup.ps1` (headless)

Idempotent, 100% headless bootstrap. Core install is flagless; everything else is opt-in.

| Command | What it installs |
|---|---|
| `.\setup.ps1` *(core)* | GPU/disk preflight; winget-installs host tools (Crush, Chatbox, uv); deploys `crush.json` to `~/.config/crush`; seeds the memory store; prompts to merge `llama.vscode-settings.json` and to add the Chatbox provider; verifies LLM assets present. |
| `.\setup.ps1 -Media` | The uncensored image+video stack, fully headless: .NET 8 SDK + git, clone+build SwarmUI, MagicPrompt extension, ComfyUI backend via the `InstallConfirmWS` WebSocket (no GUI wizard), **lean** models (Z-Image Turbo, Wan 2.1 1.3B), wires MagicPrompt → `:8013`. |
| `.\setup.ps1 -Media -Models full` *(optional, ~90–100 GB)* | Adds the quality tier: Wan 2.2 14B MoE (T2V+I2V) + 5B + encoders/VAEs; Wan2.2-Lightning LoRAs; Z-Image Base; Chroma; 4x-UltraSharp; Qwen-Image-Edit-2511; ACE-Step 1.5 music; LTXV; HunyuanVideo-Foley node+workflow; the `:8013` rewriter GGUF. |
| `.\setup.ps1 -Tts` *(optional)* | Chatterbox uncensored TTS + voice cloning on `:8004` — cu128 torch venv, Perth watermark stripped, protobuf pinned to 4.25.5. |
| `.\setup.ps1 -Stt` *(optional)* | Parakeet local STT on `:8005` — own venv with onnx-asr[cpu,hub] + FastAPI (~300 MB, CPU EP). |

*The MagicPrompt `<mpprompt:…>` rewriter is auto-configured entirely via SwarmUI's API during `-Media -Models full` (backend "OpenAI API (Local)", baseurl `:8013`, cinematographer instruction).*

---

## Agent harness convention

| Feature | What it does | How to use |
|---|---|---|
| **Root `AGENTS.md`** (the project's own brief) | DokiDex's filled-in agent brief: the build/test/run commands, the **PowerShell-not-bash / no-Docker / no-WSL** rule, the one-GPU-group rule, the "new service ⇒ add to `$Services`/`$Profiles` + a `start-*.ps1` + a guarded `verify.ps1` smoke" rule, the git-ignore list (weights/build output), the smallest-change discipline, and the persistent-memory protocol. | Already in the repo root; agents read it automatically. |
| **`harness/AGENTS.md` template** | The blank per-repo version of the same brief to drop into other working repos. | Copy `harness/AGENTS.md` into each working repo and fill in. |

**Eval gate & rejected-model history:** Crush won the coder bake-off; the docs record the losers — Claw Code (45%, flaky tool calls), Nemotron-Cascade-2 (45%), Qwen3-Coder-Next-REAP (broken tool calls). Qwen3-Coder-30B is the measured best 32 GB fit. The golden-task suite (`evals/`) is the gate for any future model swap.

---

## See also

Full architecture → [`docs/how-it-works.md`](docs/how-it-works.md) · exact API call for every capability → [`docs/media-recipes.md`](docs/media-recipes.md) · every design call + eval gate → [`docs/decisions.md`](docs/decisions.md).
