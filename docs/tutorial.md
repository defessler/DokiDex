# DokiDex — Full User Guide

A hands-on tour of **everything you can do** with DokiDex: the **DokiGen Studio** web app (Create, Chat, Director, Cast, Voice, Flow, Scene, Library, Models, Status, Memory), the **`doki` CLI**, the **control panel**, and **setup**. Where a feature has an exact API call or model-param recipe, this guide points you to the reference docs rather than repeating them.

- **In a hurry?** → [Quick Start](quickstart.md) (make your first image/chat/video in 5 minutes).
- **Just want the coding agent?** → [wiki/7-quick-start.md](wiki/7-quick-start.md).
- **Reference indexes:** [Feature index](wiki/9-features.md) · [Capabilities](CAPABILITIES.md) · [Exact API calls](wiki/11-media-recipes.md) · [Architecture](wiki/10-how-it-works.md) · [Every design call](decisions.md).

---

## 1. The mental model (read this first)

DokiDex is a **fully-local, single-user AI studio** on one machine (RTX 5090 · 32 GB VRAM · 64 GB RAM · Windows 11). Nothing leaves the box at runtime except optional web search.

Four pieces work together:

| Piece | What it is | How you touch it |
|---|---|---|
| **Control panel** | A native WPF cockpit — boot, live status, GPU meter, mode switching, logs. | `DokiDex.lnk` / `.\doki.ps1 panel` |
| **DokiGen Studio** | The web app — generate media, chat, manage your library. | A panel page, or **http://127.0.0.1:5111** |
| **`doki` CLI** | The control plane + a text→media one-liner. | PowerShell in the DokiDex folder |
| **Services** | The LLM, media, speech, and embedding servers. | Started/stopped by mode (below) |

**The one rule that shapes everything:** 32 GB can't hold the LLM brain *and* the image/video engine at once. So services split into two mutually-exclusive **GPU groups** and DokiDex runs **one mode at a time**:

| Mode | For | Services (port) |
|---|---|---|
| **agent** *(default)* | Chat · voice · Director · Cast | `llama-swap` :8080 · `tts` :8004 · `stt` :8005 · `embed` :8090 |
| **coexist** | Coding + live editor autocomplete | `llama-swap` :8080 · `fim` :8012 · `embed` :8090 |
| **media** | Image · video · music · edit · foley | `media` (SwarmUI) :7801 · `prompt-rewriter` :8013 |

Switching modes **evicts the other group first** (it's confirmed in the UI). Switch from the panel mode switcher, the studio's **Status** view, or `.\doki.ps1 up <mode>`.

---

## 2. Install & first run

DokiDex ships as **one self-contained Windows exe** (no cloned repo required).

1. **Run the installer exe.** First launch plays the boot sequence, then — if no DokiDex home is found — opens the **Setup Wizard**:
   - **Fresh install** — pick a location; it runs the bootstrap headlessly.
   - **Adopt existing** — point it at an existing DokiDex folder (models/stack already there).
2. **It self-manages from there**, and **auto-updates** itself from GitHub releases (see [§17](#17-updating)).

**From the repo** (developer path), the bootstrap is `setup.ps1` — idempotent and 100% headless. Core install is flagless; capabilities are opt-in:

| Command | Adds |
|---|---|
| `.\setup.ps1` | Core: prereqs, LLM/chat/code configs, memory store |
| `.\setup.ps1 -Media` | SwarmUI + ComfyUI + **lean** image/video models (~50 GB) |
| `.\setup.ps1 -Media -Models full` | The quality kit: Wan 2.2, Z-Image Base, ACE-Step, Qwen-Image-Edit, LTXV, Foley, the `:8013` rewriter (~90–115 GB) |
| `.\setup.ps1 -Tts` / `-Stt` | Chatterbox TTS `:8004` / Parakeet STT `:8005` |
| `.\setup.ps1 -Kokoro` | Kokoro-82M fast TTS `:8006` (alternative engine) |
| `.\setup.ps1 -Vision` | A local vision model → powers Describe/Verify and image-in-chat |
| `.\setup.ps1 -Train` / `-Demucs` / `-Sam` / `-Ocr` | LoRA trainer · audio stems · click-to-mask · scanned-PDF OCR |
| `.\setup.ps1 -FaceId` / `-Pulid` / `-InfiniteTalk` / `-LatentSync` / `-TtsSuite` | Gated specialty pipelines (face identity, talking-video, lip-sync, 15-engine TTS) — see [§14](#14-the-doki-cli) |

> Large coder GGUFs are fetched out-of-band into `models\` (size/network risk); `setup.ps1` warns if they're missing. Everything else is automatic.

**Launching day-to-day:** double-click **`DokiDex.lnk`** (console-free) or run `.\doki.ps1 panel`.

---

## 3. GPU modes in practice

You'll spend your time in **agent** (talk/voice) or **media** (make pictures/video). The rule of thumb:

- **Chatting, using Director/Cast, generating speech?** → **agent**.
- **Generating images, video, music, edits, foley?** → **media**.
- **Coding in an editor with autocomplete?** → **coexist**.

You rarely switch by hand: hit **Generate** in the studio and it offers to flip to media; the panel's switcher shows a **32 GB-headroom** readout and a **WILL STOP / WILL START** confirmation before evicting anything. The first `up media` is slow (ComfyUI extracts + one-time weight downloads); later starts are quick.

---

## 4. The control panel (cockpit)

The panel is your operations dashboard. It polls status in-process (~2 s) and shells `doki.ps1` only for lifecycle actions.

- **Service cards** — grouped LLM (cyan) / Media (gold); each shows healthy / starting / stopped / crashed / not-installed, port, and version/update badge. Start/Stop/Restart per service; **Open web UI** where one exists.
- **Mode switcher** — AGENT / COEXIST / MEDIA with the headroom + eviction confirm described above.
- **Coder model-swap chips** — click to warm-load `coder-fast` / `coder-big` / `coder-fast-lite`.
- **GPU trust-meter** — used/total GB, %, temp, watts, fan; low-headroom and hot warnings.
- **⚡ test** — one-click smallest real generation per modality (chat, FIM, rewriter, TTS, image) with ✓/✕ + elapsed ms.
- **Live logs** — tails each service's stdout/stderr with filter, pause, and severity coloring.
- **DokiGen Studio** — the web app, hosted in-process (also at :5111).

---

## 5. The DokiGen Studio — overview

Open the **DokiGen Studio** panel page, or browse **http://127.0.0.1:5111**. The studio UI always loads; individual features light up when the matching **mode** is running (it'll tell you when it isn't).

The left nav has a **Home** command center plus eleven areas, covered below:

**Make:** [Create](#6-create--generate-anything) · [Director](#82-director) · [Cast](#81-cast) · [Voice](#9-voice) · [Flow](#84-flow) · [Scene](#83-scene)
**Talk:** [Chat](#7-chat--the-assistant)
**Manage:** [Library](#10-library--manage-your-outputs) · [Models](#11-models--manage-checkpoints) · [Status](#12-status--health--modes) · [Memory](#13-memory--long-term-facts)

**Home** itself is a guided hub, not just a launcher: state-aware capability cards (with a live "▲ Ready"/"needs setup" badge and clickable starters) for every area above, a **Code** group with a `doki code` card, and "dark-feature" cards for things that were already shipped but easy to miss — **LoRA Training**, **Compare bases**, **Batch (CSV)**, **Image Set**, **Pitch-deck export**, and **Inpaint / SAM click-to-mask** (each links straight to where it lives). A **Help** view (nav, far right) renders this whole docs corpus in-app — README, quickstart, tutorial, capabilities, and the wiki — and **Ctrl+K** (or **⌘K**) opens a command palette to jump to any view or run any action from anywhere; press **?** for the full keyboard-shortcut overlay.

---

## 6. Create — generate anything

**Create** is the composer. Pick a **kind**, write a prompt, hit **Generate**. *(Needs **media** mode.)*

### The six kinds

| Kind | Makes | Notes |
|---|---|---|
| **image** *(default)* | A still image | Z-Image (fast Turbo or quality Base) |
| **video** | A silent video clip | Wan 2.2 (fast LTXV / default 5B / quality A14B) |
| **music** | An audio track | ACE-Step — style, optional lyrics, BPM, duration |
| **edit** | An instruction edit of an image | Qwen-Image-Edit; **needs an init image** |
| **i2v** | A video animated from a still | Generate/supply a frame, then animate it |
| **foley** | A video **with synced sound effects** | One pass, video + audio muxed |

> Exact models & params for each kind: [wiki/9-features.md](wiki/9-features.md) and [wiki/11-media-recipes.md](wiki/11-media-recipes.md). LTX-2.3 (native synced **audio+video** in one pass) is available today from the [CLI](#14-the-doki-cli) as `doki gen -Ltx`; a Studio pill for it is on the roadmap.

### The basic path

1. Pick the **kind** pill (image is default).
2. Type a **prompt** — `__wildcards__` and saved `@name` snippets are supported.
3. (Optional) set **aspect**, **seed** (blank = random), **count**, or pick a **Model** (or **✨ auto** to route by prompt).
4. Toggle **Fast** for a seconds-quick draft.
5. **Generate** → a card appears in **Results** with a live preview, then the finished artifact moves to the **Library**.

### Power features (when you want control)

- **Quality modifiers:** **Refine** (hi-res fix: upscale + regen detail), **Upscale** (4× pure upscale, no regen), **Face** (face refinement), **Realism** (a realism LoRA). *(stills/edit/i2v as applicable.)*
- **Model & router:** the **Model** dropdown lists your installed bases; **✨ auto** picks the best fit for the prompt; **Compare bases** renders the same prompt on every installed base side-by-side.
- **LoRA mixer:** check installed LoRAs and set per-LoRA weights (mixed in as `<lora:name:weight>`).
- **ControlNet:** stack up to 3 units (model + preprocessor: canny/depth/openpose/scribble + strength). Unit 1 can be driven by the **Sketch** canvas.
- **The edit surface** *(edit kind)* — three tabs:
  - **Sketch** — draw structure/masks; **denoise** slider sets how closely the result follows your drawing; **Live render** previews as you draw.
  - **Inpaint** — paint the exact region to change (magic-wand or, with `-Sam`, semantic click); **remove bg** drops everything but the subject.
  - **Outpaint** — extend the canvas outward by 25%/50% in any direction and let the model fill it.
- **Init image** — load a picture for img2img / inpaint / i2v; **as style ref** uses it as an IP-Adapter style/subject reference instead.
- **Video/i2v camera rig:** **Camera** presets (dolly, orbit, crane, bullet-time, handheld…), Pan/Tilt/Zoom/Roll sliders, an optional **end frame** (FLF2V), and frame **Interpolate** (RIFE/FILM/GIMM ×2/×4, if installed).
- **Music controls:** **Lyrics** (blank = instrumental), **Duration**, **BPM**, **hi-fi** (ACE-Step XL base, slower).
- **Steer-rewrite:** type an instruction ("make it night, add a red scarf") and let the LLM transform your prompt.
- **Explore ×8:** fan the prompt into 8 fast variations. **✨ Animate 2D:** an image→video preset tuned for illustration/anime.
- **Batch (CSV)** and **Image Set:** run many prompts at once (one row/line each); Image Set locks a shared style + aspect across the set.
- **Live:** re-render on a turbo pass as you type/draw (GPU-exclusive; media mode). Live renders are ephemeral — they don't clutter the Library.

### Where outputs go

Finished (non-ephemeral) generations land in your **gen folder** and appear in the **Library** with a sidecar recording the prompt, kind, seed, and parent lineage (so **Refine**/**Explore** variants trace back to their source).

---

## 7. Chat — the assistant

An uncensored, persona-first local assistant with tools, vision, documents, memory, and voice. *(Needs **agent** mode; the LLM at `:8080`.)*

### Basics

Type a message, **Ctrl+Enter** to send. Pick a **Speed** tier:

| Speed | Model | Feel |
|---|---|---|
| **fast** | `coder-fast` (Qwen3-Coder-30B) | Snappy, ~95% of turns |
| **quality · slower** | `coder-big` (gpt-oss-120B) | Stronger reasoning, much slower (it's CPU-offloaded) |

Conversations persist; switch threads from the **Conversation** dropdown, or **+ New chat**. **Export** saves a thread to disk.

### Personas (uncensored)

**+ new persona** → give it a **name** and a **system/behavior** prompt (no content filter on the persona prompt), optionally attach a **Lorebook** (world-info entries that activate on keywords) and a **Voice** for readback. Pick it from the **Persona** dropdown to chat in character.

### Vision (let it see)

Click **+ image**, pick a Library image — attaching an image **automatically uses the vision model** (Qwen3-VL) for that turn, regardless of the Speed setting. Ask it to describe, transcribe, or critique the picture.

### Tools mode (🔧)

Toggle **🔧 tools** and the assistant can call:

- **search_library** — find your own past generations by description.
- **web_search** — keyless DuckDuckGo lookup.
- **code_search** — semantic search over this project's source.
- **generate_image / edit_image** — **make or edit a picture in-thread**. The job queues and surfaces inline (queued → rendering → done) without you leaving Chat.

> Tools mode is non-streaming and can't be combined with an attached image (vision is its own single-turn path).

### Knowledge base (chat with your documents — RAG)

**+ knowledge base** → paste text or upload `.txt/.md/.pdf/.docx`. Attached docs show **[RAG ON]** and their most relevant chunks are injected into every turn. Make a reusable **knowledge library** to share docs across conversations.

### Voice readback

Each assistant reply has a **🔊** button to synthesize and play it (uses the persona's voice if set). Needs TTS (`:8004`) up — i.e. agent mode.

---

## 8. The production tools

Four creative views that compose generations. **Director** and **Cast** lean on the LLM (agent mode); all four ultimately submit to **Create** (media mode).

### 8.1 Cast

**Multi-character image composition.** Write a **base scene** ("2girls, a sunny park, anime style"), add up to **6 characters** (each with its own description + a coarse 9-cell **position**), and an optional **relationship** ("A hugs B"). **Compose & preview** compiles a SwarmUI regional prompt (`<object:…>` tags that keep each character's attributes in their own region, preventing attribute bleed); **Generate** renders it. Relationship phrasing can be literal or LLM-enhanced.

### 8.2 Director

**Script → shotlist → images.** Paste an idea or treatment, set a **shot count** (1–20), pick a tier, and **Storyboard**. The LLM returns an ordered list of shots, each with an editable image prompt. Tweak any prompt, then **→ generate** per shot (or all). Because it's text-only, you can storyboard in agent mode and generate in media mode.

### 8.3 Scene

**3D blockout → depth → ControlNet.** Place boxes in 3D (x/y/z/size), set the camera (distance + FOV), and **Render depth** — a server-side rasterizer (no GPU) produces a grayscale depth map. **Use as ControlNet depth input →** pushes it into Create's ControlNet unit; pick a depth ControlNet model and generate a structure-guided image that respects your layout.

### 8.4 Flow

**A node-lite pipeline (DAG).** Add steps, each with a **prompt** and **kind**, and wire **dependencies** between them. **Run flow** validates the graph (rejects cycles), computes execution order, and queues each step in dependency order. Note: it's a **scheduler, not a data pipeline** — later steps don't automatically receive earlier steps' outputs; reference them yourself (e.g. via an init image) if you need that.

---

## 9. Voice

Local, uncensored text-to-speech (Chatterbox on `:8004`). *(Needs **agent** mode.)* Output saves to the **Library**.

- **Simple TTS:** type text, pick a **Voice** (or *default*), set **Expressiveness** and **Guidance**, **Speak**. The clip plays and is saved.
- **Pronunciation dictionary:** add `Name=PRONUNCIATION` lines (e.g. `Caelum=KYE-lum`) — applied as whole-word substitutions before synthesis.
- **Multi-speaker dialogue:** write a script, one turn per line (`HERO: [excited] We made it!`). Delivery tags like `[excited]`, `[whisper]`, `[angry]`, `[sad]` set the prosody and are stripped from the spoken text. A per-speaker **voice picker** appears as you type names; **Render** synthesizes each line in its speaker's voice and concatenates them into one clip.
- **Voice cloning:** zero-shot — drop a reference clip (`.wav/.mp3/.flac/.ogg`) into Chatterbox's voice folder (`tts/Chatterbox-TTS-Server/reference_audio` or `voices`); the filename becomes a selectable voice. (Restart the TTS server so it's scanned.)

> **Engines:** Chatterbox is the default. A lighter **Kokoro** engine (`:8006`, `-Kokoro`) exists but isn't exposed in the Voice UI yet. **STT** (Parakeet, `:8005`) runs in agent mode for the API, but there's no microphone surface in the studio yet.

---

## 10. Library — manage your outputs

Everything you generate lands here.

- **Browse & filter:** search prompts; filter by **kind** and **view** (active / ★ favorites / untriaged / 🗑 trash).
- **Keyboard triage:** click a card, then **F** favorite · **X** trash · **U** clear · **←/→** move.
- **Per-item actions:** **describe** (vision: image→prompt) · **verify** (vision: does it match its prompt?) · **palette**/**recolor** · **last frame**/**extend** (video) · **stems** (split music — needs ffmpeg/`-Demucs`) · **remix** (reload into Create) · **save** · **del**.
- **Saved searches** and **variation lineage** (which cards derived from which).
- **LoRA training:** select images from the current view and queue a training run (needs `-Train`/kohya).
- **Reuse:** any Library image can be a **Chat** attachment (vision) or a **Create** init/i2v input.

> describe/verify need the **vision model** installed (`-Vision`) and agent mode; without it they return a clear "start agent / install vision" hint.

---

## 11. Models — manage checkpoints

- **Browse** installed bases, LoRAs, and ControlNets grouped by capability, with size, tier, and install status.
- **Install / remove** from the catalog (download progress polls live).
- Installed **image models** populate Create's **Model** dropdown and feed the **✨ auto** router; installed **LoRAs**/**ControlNets** populate the mixer and unit pickers.
- **Text models:** a parallel section for the LLM/coder side — install or delete GGUFs (`coder-fast`, `coder-big`, `reasoning`, `vision`, `fim`, `embed`, plus bake-off **candidates**) with SHA-256 verification on every download.
- **Tiers:** a table of every speed/quality tier (fast · quality · reasoning · vision) showing whether it's configured in llama-swap, present on disk, and currently loaded, with a **warm** button to preload it.
- Both tables show an **eval** badge (e.g. `14/15`) — the latest-per-task golden-suite score for that model — when eval data exists; ungated candidates show **ungated** instead.

---

## 12. Status — health & modes

- The **GPU pill** shows the active mode and VRAM use.
- **Mode buttons** (AGENT / COEXIST / MEDIA) switch the GPU group on click.
- **Service cards** show each service's health, port, VRAM, and loaded model — the place to answer "why isn't X ready?"

---

## 13. Memory — long-term facts

A persistent fact store (SQLite FTS5) that the assistant recalls in **every** chat — separate from a chat's knowledge base.

- **Add** a fact (with optional tags); **delete** stale ones.
- Facts are injected automatically into each chat turn — no manual attach.
- It's the same store the coding agent writes to via its `memory_save`/`memory_search` tools (see [wiki/9-features.md](wiki/9-features.md#persistent-memory)). The panel shows "unavailable" until the store exists (it's created on first save or by `python serving/memory-mcp/seed.py`).

---

## 14. The `doki` CLI

Everything the panel does, plus a text→media one-liner. Run from the DokiDex folder in PowerShell.

| Command | Does |
|---|---|
| `doki up [agent\|coexist\|media]` | Start a mode (default `agent`); evicts the other GPU group first |
| `doki down` | Stop every managed service, free the GPU |
| `doki status [json]` | Service + health table (`json` = machine-readable) |
| `doki restart [service\|profile]` | Restart one service or a whole mode |
| `doki start\|stop <service>` | Per-service control (group-guarded) |
| `doki logs <service> [-Clear]` | Tail a service's logs |
| `doki gen "<idea>" […]` | **Text→media** (see below) — needs `up media` |
| `doki code ["<task>"]` | **Local coding agent** in the current directory (see below) — needs `up agent` |
| `doki verify [-Gated]` | Full-stack smoke test across modes |
| `doki doctor` | Environment + install diagnostics |
| `doki index` | Rebuild the codebase RAG index for `code_search` |
| `doki test` | Fast no-GPU unit suite |
| `doki panel` | Launch the control panel |
| `doki help` | List every command on one screen (bare `doki` still defaults to `status`) |

### `doki gen` — text→media

```
doki gen "<idea>" [KIND] [MODIFIERS] [PARAMS]
```

**Kinds (one):** *(none)* image · `-Video` · `-Music` · `-Edit` · `-I2v` · `-Foley` · `-Ltx` · and the gated specialty kinds `-FaceId` · `-Pulid` · `-InfiniteTalk` · `-LatentSync` · `-Speak`.

The CLI is the **power surface** — it exposes kinds the studio doesn't yet, notably:

| Kind | Makes | Gate |
|---|---|---|
| `-Ltx` | **LTX-2.3**: native synced **audio+video** in one pass | `-Media -Models full` |
| `-FaceId` | InstantID face-identity transfer (SDXL) — needs `-InitImage <face>` | `setup.ps1 -FaceId` |
| `-Pulid` | PuLID-Flux face identity (FLUX.1-dev) — needs `-InitImage <face>` | `setup.ps1 -Pulid` |
| `-InfiniteTalk` | Talking-video from a portrait — needs `-InitImage` + `-Audio` | `setup.ps1 -InfiniteTalk` |
| `-LatentSync` | Lip **re-sync** an existing clip to new audio — needs `-Audio` | `setup.ps1 -LatentSync` |
| `-Speak` | TTS-Audio-Suite speech (15 engines + RVC) — `-Engine`, optional `-Audio` ref voice | `setup.ps1 -TtsSuite` |

**Modifiers:** `-Fast` · `-Quality` · `-Upscale` · `-Refine` · `-Face` · `-Realism` · `-Raw` (skip the prompt rewriter).
**Params:** `-Seed` · `-Count` · `-Aspect` · `-InitImage` · `-MaskImage` · `-EndImage` · `-Audio` · `-Strength` · `-Negative` · `-Lyrics` · `-Duration` · `-Bpm` · `-Lora "name:weight,…"` · `-Segment "face,hands:0.6"` · `-ControlNets <json>` · `-Reference`/`-RefWeight` · `-Upscaler` · `-Tile` · `-Model` · `-Workflow` · `-Out <file>` · `-NoOpen`.

**Examples:**

```powershell
.\doki.ps1 up media

.\doki.ps1 gen "a neon koi dragon"                              # image
.\doki.ps1 gen "a spaceship launching" -Video -Quality -Refine # quality video
.\doki.ps1 gen "lofi beat" -Music -Bpm 90 -Duration 30         # music
.\doki.ps1 gen "make the sky orange" -Edit -InitImage shot.png # edit
.\doki.ps1 gen "a thunderstorm over the sea" -Ltx              # LTX-2.3 video + audio
.\doki.ps1 gen "Welcome to DokiDex" -Speak -Engine Higgs -Out hello.wav
```

> Full per-kind recipes (models, steps, cfg): [wiki/9-features.md](wiki/9-features.md) and [wiki/11-media-recipes.md](wiki/11-media-recipes.md).

### `doki code` — local coding agent

A terminal coding agent that mirrors the Claude Code CLI, running the local coder model via llama-swap. The workspace is your **current directory** — `cd` into any project and run it. Content streams live by default as the model writes (`--no-stream` forces the old blocking-per-turn path); **Esc** or **Ctrl+C** interrupts mid-turn without exiting the REPL.

```powershell
.\doki.ps1 up agent           # 1. load the coder model (:8080)
cd path\to\your\project       # 2. go to the workspace
.\doki.ps1 code                # interactive REPL (type a task at the › prompt)
.\doki.ps1 code --continue     # ...resume the workspace's most recent saved session
.\doki.ps1 code "<task>"       # one-shot: run the task and exit
```

The agent builds itself on first run (`DokiDex.Cli`). A warning appears if the coder model isn't serving yet.

**Repo orientation:** on startup it auto-loads the first-found `DOKI.md` → `AGENTS.md` → `CLAUDE.md` at the workspace root, plus a depth-2 directory tree and a `git status` snapshot, into a fixed system message — so it isn't Grep-blind for the first several turns like a bare model would be. `/init` explores the repo and writes (or improves) a `DOKI.md` at the workspace root through the normal approval gate.

**Tools the agent can use:**

| Tool | What it does | Approval? |
|---|---|---|
| **Read** | Read a file by workspace-relative path, with optional offset + line limit for large files | No |
| **Grep** | Regex search over files, with optional sub-directory scope and file-glob filter | No |
| **Edit** | Replace an exact block of lines in an existing file (SEARCH/REPLACE blocks) | Yes — shows colored diff |
| **Write** | Create a new file (or fully overwrite an existing one) | Yes — shows colored diff |
| **Bash** | Run a PowerShell command in the workspace | Yes — shows the command |
| **WebSearch** / **MemoryRecall** *(opt-in, off by default)* | Keyless DuckDuckGo lookup / recall your saved long-term memory notes | No — read-only |

**Per-action approval:** every Edit, Write, and Bash call shows a preview and waits:

```
Allow Edit? [y]es / [a]lways / [n]o:
```

Default (Enter or any other key) is **no** — always the safe choice. `[a]lways` now saves a **persisted permission rule** (below) instead of just a one-off session bypass; on a Bash call it asks a follow-up — **[c]ommand exact** / **[p]refix** ("first two words *") / **[t]ool-wide** — so you can allow just `git status`, any `dotnet test …`, or all of Bash going forward.

**Edit protocol:** the model emits `<<<<<<< SEARCH / ======= / >>>>>>> REPLACE` blocks. A two-stage fuzzy applier tries (1) exact whole-line match, then (2) whitespace-flexible match. On a miss it shows the actual nearby lines from the file so the model can self-correct and retry.

**Context & compaction:** a dim `~Nk / 32k ctx · Ns · N tok/s` meter prints after every turn (32k is the healthy working-set budget; the model's real hard window is 131k). `/compact [instructions]` summarizes older history down to free up context (optionally focused, e.g. `/compact the auth refactor`); the session also **auto-compacts** past ~40k estimated tokens before your next turn runs. `/context` shows the full system/history/total breakdown.

**Sessions:** every turn is saved automatically to `%USERPROFILE%\.doki\sessions\<workspace-hash>\<timestamp>.json` — outside the repo, so there's nothing to gitignore. `doki code --continue` resumes the workspace's most recent session; inside the REPL, `/resume` (alias `/sessions`) lists saved sessions newest-first and `/resume <index>` loads one; `/export [file]` writes the current transcript as markdown.

**Permissions:** `[a]lways` and `/permissions allow|deny <rule>` persist rules to `%USERPROFILE%\.doki\permissions` — a rule is a bare `Tool` (e.g. `Read`, `Edit`) or `Tool(specifier)`, where the specifier is either an exact match or a `prefix *` (e.g. `Bash(dotnet test *)`). A **deny** rule always wins and short-circuits before you'd even be asked. `/permissions` (alias `/allow`) lists the current rules with numbers; `/permissions remove <n>` removes one.

**Plan mode:** `/plan` switches to read-only exploration — only Read/Grep are offered to the model, and any proposed edit is shown but **not applied**; the prompt becomes `plan› ` while it's on. `/act` (or `/plan off`) restores normal editing.

**Custom commands:** drop a template at `.doki/commands/<name>.md` (workspace-local, shared with your team if committed) or `%USERPROFILE%\.doki\commands\<name>.md` (personal, global) and it becomes `/<name> [args]` — its text runs as your next turn, with every `$ARGUMENTS` replaced by whatever you typed after the command name. A workspace command shadows a global one of the same name; built-in commands always win.

**Input shortcuts:** `@rel/path` anywhere in a message inlines a bounded window of that file for the model (up to 3 mentions per message) — e.g. "fix the bug in `@src/app.cs`". A line starting with `!` runs a shell command **directly**, with no model round-trip and no approval prompt (you typed it yourself).

**Opt-in tools:** `WebSearch`/`MemoryRecall` are off by default to keep the tool set small (open models lose tool-selection accuracy as it grows). `/tools` shows the current state; `/tools web on` / `/tools web off` toggles it for the session.

**Slash commands in the REPL:**

| Command | Does |
|---|---|
| `/help` | Show available commands (built-in and custom) |
| `/model [<name>]` | Switch or show the active model (`coder-fast` \| `coder-big` \| `fast-candidate-gptoss20b`) |
| `/diff` | Show this session's working-tree changes |
| `/undo` | Revert the last file change this session |
| `/init` | Explore the repo and write/improve a `DOKI.md` |
| `/clear` | Clear the conversation context (the workspace stays the same) |
| `/cwd` | Show the workspace root |
| `/compact [instructions]` | Summarize older history to free up context |
| `/context` | Token-budget breakdown (system / history / total) |
| `/resume [index]` (alias `/sessions`) | List saved sessions, or load one by index |
| `/export [file]` | Write the transcript as markdown |
| `/permissions` (alias `/allow`) | List/add/remove persisted allow/deny rules |
| `/status` | llama-swap reachability, loaded model, configured tiers |
| `/usage` (aliases `/cost`, `/stats`) | This session's turns, tokens, wall time, avg tok/s |
| `/plan` / `/act` | Enter/exit read-only plan mode |
| `/tools [web on\|off]` | Show or toggle the opt-in WebSearch/MemoryRecall tools |
| `/exit` | Exit the REPL |

Plus any custom `.doki/commands/*.md` commands you've defined.

**Working with edits:** changes land as plain working-tree modifications. Review them with `/diff` or `git diff` at any time. `/undo` restores the most recent change from this session (or deletes a file that Write created); your own git history is the durable backstop.

**Scripting (one-shot):** `doki code -p "<task>"` runs a single turn and exits — code `1` on failure, `0` on success — for use in scripts. Pipe input in: `git diff | doki code -p "review this"`. Add `--output-format json` to print one machine-parseable `{result, ok, duration_ms}` object on stdout instead of the normal colored console output.

**Default model:** `coder-fast` (Qwen3-Coder-30B-A3B). Switch with `/model coder-big` for the 120B heavy-hitter, or `/model fast-candidate-gptoss20b` to try the Devstral eval candidate (see [docs/mistral-2026-06.md](mistral-2026-06.md)).

---

## 15. Services & ports reference

All bind to **127.0.0.1** only (no LAN exposure).

| Service | Port | Group | Role |
|---|---|---|---|
| `llama-swap` | 8080 | llm | OpenAI-compatible LLM router (chat/code/vision) |
| `fim` | 8012 | llm | Fill-in-the-middle autocomplete (coexist) |
| `embed` | 8090 | llm | Code/KB embeddings (CPU, 0 VRAM) |
| `tts` | 8004 | llm | Chatterbox TTS + voice cloning |
| `stt` | 8005 | llm | Parakeet speech-to-text |
| `kokoro` | 8006 | llm | Kokoro fast TTS *(optional)* |
| `media` | 7801 | media | SwarmUI (image/video/music/edit) |
| `prompt-rewriter` | 8013 | media | `<mpprompt:…>` lazy-prompt expander |
| **DokiGen Studio** | **5111** | — | The web app (hosted by the panel) |

---

## 16. Troubleshooting & gotchas

| Symptom | Fix |
|---|---|
| A studio feature says it's not reachable | Start the right **mode** (chat/voice → agent; gen → media), from the panel or `doki up`. |
| First `up media` takes a minute+ | One-time ComfyUI extract + weight downloads. Later starts are fast. |
| First message to **quality** chat hangs | `coder-big` is ~60 GB loading from disk; later turns are fast. |
| Editor autocomplete + agent OOM | Use **coder-fast-lite** in `coexist` (full coder-fast won't fit beside FIM). |
| describe/verify or image-in-chat does nothing | Install the vision model (`setup.ps1 -Vision`) and be in agent mode. |
| Kokoro TTS errors at synth | It needs the eSpeak-NG DLL — verify `C:\Program Files\eSpeak NG\libespeak-ng.dll`. |
| A `-FaceId`/`-Pulid`/`-InfiniteTalk`/`-LatentSync` kind isn't recognized | It's gated — run the matching `setup.ps1` flag first. |
| Stems / LoRA-training actions missing | Need `-Demucs` (ffmpeg) / `-Train` (kohya). |

Run `.\doki.ps1 doctor` for a full environment/install diagnosis, and `.\doki.ps1 verify` for a live capability smoke test.

---

## 17. Updating

The control panel **auto-updates itself**: on launch it checks the project's GitHub releases, and if a newer version exists it downloads, verifies, and swaps the exe in place (no admin, no installer). The media stack (SwarmUI) and `llama-swap` update via the panel's **Check Updates** action.

*Maintainers:* cut a release with `git tag vX.Y.Z && git push origin vX.Y.Z` — CI builds the self-contained exe and publishes it as the release the updater consumes.

---

## See also

- [Quick Start](quickstart.md) — the 5-minute app intro
- [Feature index](wiki/9-features.md) · [Capabilities](CAPABILITIES.md) — reference catalogs
- [Media recipes](wiki/11-media-recipes.md) — exact API call for every capability
- [How it works](wiki/10-how-it-works.md) — architecture · [Decisions](decisions.md) — every design call + eval gate
- [Coding quick start](wiki/7-quick-start.md) — the local coding agent (Crush)
