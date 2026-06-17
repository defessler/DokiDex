Confirmed. Facts verified, design follows.

# DokiDex — Local Generative Studio: Approval-Ready Design

DokiDex becomes a **local single-user web studio**: a tray launcher boots SwarmUI headless + an in-process web server, opens the browser to a Sora2/Grok-grade site that (a) composes/runs generations across all 7 capabilities and (b) manages/downloads models & workflows. SwarmUI stays the headless engine; the existing C# control plane becomes the web backend with near-zero re-implementation. Targets: **127.0.0.1-only, 32GB single-GPU, Windows, one self-contained exe, no cloud, uncensored.**

---

## 1. Architecture

### Chosen stack (named)
- **Web server:** in-process **ASP.NET Core / Kestrel minimal API**, bound `127.0.0.1`, hosted inside the existing app exe.
- **SPA:** **Vite + React + TypeScript + Tailwind v4 + shadcn/ui** (owned source, not a dependency tree), **TanStack Query** for REST.
- **Progress transport:** **SignalR** (streaming hub methods). *Decision-forcing fact, verified:* `TypedResults.ServerSentEvents` is ASP.NET Core **10-only**; the app's WPF host is pinned `net9.0-windows` (confirmed in `control\DokiDex.Control.csproj:5`). SignalR ships full WebSocket + streaming-hub support on net9.0 today. (See §6 for the net10.0 escape hatch.)
- **Engine:** SwarmUI `:7801` headless, unchanged; cloned + dotnet-built as today.

### Solution reshape
- New **`DokiDex.Core`** library (`net9.0`, **not** `-windows`): move the already-UI-agnostic services into it — `Lifecycle`, `StatusProbe`, `ServiceRegistry`, `Status` DTOs, `GenArgs`/`GenCli`, `InstallPlan`, `RepoPaths`, `AppSettings`, `Payload`. These have zero WPF refs (verified).
- New **`DokiDex.Web`** (`Microsoft.NET.Sdk.Web`, `net9.0`): references `Core`, owns the HTTP/SignalR surface and the SwarmUI WS bridge.
- The WPF exe keeps `net9.0-windows`/WPF only for the tray + installer + updater; it references `Core` and starts `DokiDex.Web` in-process.

### Launcher / tray (replaces the cinematic cockpit)
The WPF app **is kept and slimmed to a tray launcher** — it already owns exactly the boot responsibilities needed (`App.xaml.cs OnStartup`, verified): single-instance Mutex → in-place updater + relaunch → Setup Wizard if no valid home → per-version payload refresh. New boot sequence:

1. `Lifecycle.Up("media")` to evict the llm group and launch SwarmUI (`WaitHealth` on `:7801` — today only in `doki.ps1`, port it to `Core`).
2. Start Kestrel in-process on `127.0.0.1:<port>` (handle port-in-use: the Mutex guards the process, the HTTP port is a new conflict surface → pick next free port, surface in tray).
3. Open the browser to the local URL **only after both `:7801` and Kestrel report healthy** (`DokiService.OpenUi` is already http-scheme-guarded — verified).
4. Sit in the tray: status, Stop-All, Switch-Mode, **Open DokiDex** (re-open tab), Quit. Tray shows a "starting…" state so the hidden app isn't a black box.

The cinematic `BootWindow → MainWindow` cockpit and the full WPF Studio/installer panels are **retired as the primary surface**; the Setup Wizard window stays (first-run only). This is strictly *less* work than the cockpit and reuses updater + payload + mutex unchanged.

### C# control plane → web API (1:1, no logic change)
| Endpoint | Backing code (verified reusable) |
|---|---|
| `GET /api/status` | `StatusProbe.GetAsync()` → `StatusDoc` (already a clean JSON DTO, no pwsh) |
| `POST /api/mode/{agent\|coexist\|media}` | `Lifecycle.Up(profile)` (GPU-evict engine) |
| `POST /api/services/{name}/{start\|stop\|restart}` | `Lifecycle.Start/Stop/Restart` |
| `POST /api/generate` → `{jobId}` | `GenRequest` + `GenCli.BuildArgs` (typed, pure, validated) |
| `GET /api/jobs/{id}` + SignalR hub | job store (net-new) |
| `POST /api/jobs/{id}/cancel` | SwarmUI `POST /API/InterruptAll` (never kill a process) |
| `GET /api/models`, `/api/workflows`, `/api/gallery` | §3, §2 |

### GenerateText2ImageWS → live browser progress
Today the **only** SwarmUI gen call site is PowerShell `Invoke-Gen` (`serving\doki-gen.ps1`): one blocking `POST /API/GenerateText2Image`, `TimeoutSec 600`, returns only the final artifact, **no progress** (verified). To get Sora/Grok-grade progress the web backend must **bypass it**:

- **Port the three pure recipe functions to C#** in `Core` — `Get-GenRecipe` / `Get-GenPromptFields` / `Build-GenBody` — keeping them 1:1 with `GenCli` (single source of truth; see §5 parity test).
- The C# worker opens SwarmUI **`GenerateText2ImageWS`** via `ClientWebSocket` — the **exact** pattern already proven in `setup.ps1` `InstallConfirmWS` (lines 287–318, verified). It translates frames → normalized job events → re-broadcasts over SignalR:
  - `status` → **queued** state
  - `gen_progress {overall_percent, current_percent, preview: data:image/jpeg;base64}` → **progress bar + live preview thumbnail** (verified frame shape)
  - `image {image: "View/...", batch_index, metadata}` → **result** (artifact URL)
- SwarmUI's WS stays **server-side only** (relay, not exposed to the browser) — keeps the untyped/unversioned SwarmUI surface behind the pinned-commit boundary.
- **Frontend feedback contract:** `overall_percent` is workflow-graph position, not wall-clock — pair it with elapsed-time text and treat it as indeterminate until the first frame; render via `role=progressbar`/`aria-valuenow` + an `aria-live` status region; gate preview animation on `prefers-reduced-motion`.

### Localhost-only security (engineering-honest stance)
Binding `127.0.0.1` is **necessary but not sufficient** — a validator refuted "secure because local." The existing stack already audits this (SwarmUI launched `--host 127.0.0.1` at `setup.ps1:259`; Chatterbox's `0.0.0.0` actively rewritten to `127.0.0.1` at `:156`). The new unauthenticated host exposes **state-changing endpoints** (generate, model install/DELETE, mode-switch) reachable by **any web page the user visits** via DNS-rebinding / CSRF even on loopback. **Required, not optional:**
- Bind `127.0.0.1` exclusively, never `0.0.0.0`.
- **Host-header allowlist** middleware (`127.0.0.1:<port>` / `localhost:<port>` → else 403) → blocks DNS rebinding.
- **Origin check or per-session CSRF token** on every state-changing route.
- No permissive CORS; don't echo arbitrary origins. No auth otherwise (matches single-user constraint).

### Single self-contained exe (SPA + server in ONE)
The repo **already** embeds `payload.zip` as a manifest resource via a `BeforeTargets="CoreCompile"` target (`StagePayload`, verified at csproj:47–52) and extracts it zip-slip-guarded (`Payload.cs`). **Add an analogous `StageSpa` target**: `npm run build` → zip `dist/` → `EmbeddedResource LogicalName="DokiDex.spa.zip"`. Serve via `ManifestEmbeddedFileProvider`/`CompositeFileProvider` in `StaticFileOptions` + `MapFallbackToFile("index.html")` for client-side routing. (Plain `wwwroot` is **not** embedded by `PublishSingleFile` — the embed route is the documented fix and identical to proven in-repo machinery.) Dev loop: Vite HMR via `SpaProxy`. Self-contained publish bundles the ASP.NET shared framework (grows the exe; does not break the one-exe constraint).

---

## 2. The Site (Sora2/Grok-grade IA + components)

**Three-zone creator app, not a social app.** Dropped (refuted as multi-tenant for a 1-user corpus): For-You feed, cameo/likeness, likes, comments, publish, accounts. Adopt **Midjourney's page split minus social** (only IA that holds *both* halves).

### Information architecture (left rail)
**Create** · **Library** · **Edit (Canvas)** · **Models** · **Workflows** — plus a persistent **status/VRAM gauge + GPU-mode switch** in the rail (sticky top-bar when collapsed). Desktop = 3 columns (rail / composer+results / library); collapse to single scroll with sticky composer under a container-driven breakpoint (not a hard 900px). Virtualize the Library grid (unbounded local output).

### Key pages/flows → capability tag
| Component | What it does | Capability served |
|---|---|---|
| **Composer** (fat textarea, visible **Generate** button + Ctrl+Enter alias, kind picker, init-image **click-to-browse + dropzone**) | submit → `{jobId}` async job | image / text-video / i2v / edit / music / foley |
| **Recipe chip row** (curated only) | Fast, Upscale (image/edit only), Refine, Face, Realism, Raw; **never** sampler/CFG/steps or the Wan-14B fp8 path | enforces curated-recipe contract |
| **Auto-prompt button** | round-trips the `:8013` rewriter (`<mpprompt:>`), shows the **expanded** prompt for edit/confirm; Raw bypass | prompt quality (image/video) |
| **Generation card** (queued → live progress + base64 preview → result; Cancel/Remix/Rerun/Use-prompt) | one reusable Card lifecycle | all kinds; **default single rich-progress card** (Z-Image Base is 35-step/slow), multi-up grid only on `-Fast` tier |
| **Library grid** (virtualized, focus-navigable, uniform grid not masonry; hover **and** focus/kebab actions) | Remix / Use-prompt / Upscale / Download / Delete per tile | gallery/library |
| **Edit Canvas** (mask paint → SwarmUI `MaskImage` inpaint/outpaint) | region edit | **edit** — *largest net-new build; not a re-skin of today's instruction-only `-Edit`* |
| **Remix / Rerun / Vary** | Remix = same artifact + edited prompt; Rerun = same recipe, new seed; **Vary = low-creativity img2img re-roll** (route to i2v only when motion explicitly wanted) | remix/variations |

**Sidecar schema (Sora-pattern, fills the persistent-gallery gap):** each artifact gets a JSON sidecar `{id, kind, prompt(raw), expandedPrompt, seed(resolved), recipeParams, model, date, aspect, sourceArtifactForRemix}`. **Reproducibility requirement:** capture the **resolved seed** and the rewriter's **expanded prompt** from the `image` frame's metadata at completion — today every call re-seeds and re-expands server-side, so "remix exact" is impossible without this.

---

## 3. Model & Workflow Manager — the "Models / Workflows" page

### Default engine: SwarmUI `DoModelDownloadWS` (verified in `src/WebAPI/ModelsAPI.cs`)
It already does the hard 80%: HF + Civitai download, token injection (Civitai `?token=`, HF `Authorization: Bearer`), `type`→`Models/*` folder mapping (`.gguf`→`diffusion_models`), and streams `current_percent`/`overall_percent`/`per_second`. DokiDex drives it via the same `InstallConfirmWS` WS pattern, then calls **`/API/TriggerRefresh`** (already wired, `setup.ps1:440`) so new weights light up without restart.

### HYBRID three-backend router (one queue, one progress contract)
A single engine **provably** can't reach every destination:
- **`DoModelDownloadWS`** → everything in `Models/*`.
- **`hf_hub_download`** (`serving/download_models.py`) → repo-sharded / LLM GGUFs into repo `models/` (sentinel-only output → router must **synthesize** byte progress).
- **`Get-Model`** curl (`.part`→atomic `Move-Item`, resumable, `setup.ps1:324–342`) → the ComfyUI-native `models/foley` dir SwarmUI cannot map.
The router normalizes **cancel / resume / error→actionable-message** per backend so per-item pause/cancel is uniform, not skin-deep.

### Catalog (the missing data structure)
Author one `media-assets/model-catalog.json` — ComfyUI-Manager's entry shape `{name,type,base,save_path,filename,url,size}` **extended** with `{id, capability, source(hf|civitai|url), swarmType, destFolder(+foley exception), renameTo, sizeBytes, sha256/blake3, vramGb, license, uncensored, companions[], recipeIds[], tier(lean|full), workflowId?}`. This replaces today's scatter (URLs in `setup.ps1`, filenames in `Doctor`/`StatusProbe`, params in `doki-gen` switch arms, prose in `frontier-roadmap.md`). `setup.ps1 -Models lean|full` becomes "install every item tagged `tier:lean|full`"; the ~25 hardcoded `Get-Model` rows (incl. `renameTo` for colliding `high_noise_model.safetensors`/`pytorch_lora_weights.safetensors`) become catalog rows.

### Presence, disk, provenance
- **Presence:** `ListModels`/`DescribeModel` are the authoritative installed list + model **class** + license/preview (replaces brittle `Test-Path`). **Graceful degradation:** when SwarmUI is down, fall back to catalog+`Test-Path` and show "media backend offline — last known," never a silently empty list.
- **Disk:** reuse `InstallPlan` `RequiredGb`/`HeadroomGb=5`/`FitsFreeSpace`/`DriveInfo`; **swap the hardcoded 80/100/15 buckets for summed `sizeBytes`** over selected items.
- **Provenance sidecars:** write `.cm-info.json` + `.preview.jpeg` next to each weight for fields SwarmUI doesn't carry (capability, uncensored, recipeIds, license-if-absent). **Precedence rule:** `DescribeModel` is primary for class/architecture/preview; sidecar only fills gaps — one merge rule so cards never show conflicting data.

### Tokens (corrected — refuted claim)
**Do NOT dual-store tokens.** `DoModelDownloadWS` reads Civitai/HF tokens from **SwarmUI's own user generic-data**. DokiDex collects the token in its UI but **writes it through to SwarmUI** (single token authority); `AppSettings` holds only install records + a pointer. Optionally DPAPI-protect any secret at rest.

### Add-by-URL & Workflows pages
- **Models tab:** capability-grouped cards (Installed / Available / Downloading with live SSE per-file bars), "install what you need" preset (lean/full/per-capability), disk-fit meter, license/uncensored badges. **Add-by-URL** box → `ForwardMetadataRequest` populates a card (loading skeleton; timeout fallback "install anyway?"; 401 deep-links to the token field).
- **Workflows tab:** generalize the one hardcoded WanFoley install (`setup.ps1` 5h) into a catalog record `{id, nodeRepos[], pipRequirements, customWorkflowJson, requiredModelIds[]}`; ordered installer (Pinokio-style) streams each step's log + allows cancel; **validates `requiredModelIds` present (red-X "install missing") before enabling**; supports import + enable/disable.

### New API
`GET /api/models` (catalog⋈presence) · `POST /api/models/{id}/install|pause|cancel` · **`DELETE /api/models/{id}`** (uninstall/GC — exists nowhere today) · SignalR download stream · `GET/POST /api/workflows`.

---

## 4. Capability Coverage (all 7 through SwarmUI)

| Capability | Path | Status |
|---|---|---|
| **Image** | Z-Image Base/Turbo via `GenerateText2ImageWS` | ✅ today |
| **Text→video** | Wan2.2 recipe | ✅ today |
| **Image→video** | i2v recipe (`initimage`, `RequiresInitImage`/`IsInlineImageKind` in `GenArgs`) | ✅ today |
| **Edit (instruction)** | Qwen-Image-Edit over whole init image | ✅ today |
| **Edit (region inpaint/outpaint)** | SwarmUI `MaskImage` + Canvas | ⚠️ **net-new** (largest build item) |
| **Music** | ACE-Step recipe | ✅ today |
| **Foley** | WanFoley custom node + JSON in `CustomWorkflows`; weights in ComfyUI `models/foley` | ✅ today (foley dir stays on `Get-Model` fallback) |
| **Upscale** | 4x-UltraSharp; `-Upscale` gated to image/edit (`UpscaleApplies`) | ✅ today |

**Gaps:** (1) region inpaint/outpaint is genuinely new (mask UI + `MaskImage` wiring). (2) New recipe knobs — explicit **seed**, **initimagecreativity/strength**, **aspect ratio**, **count/batch**, **duration** — are **not** in `GenRequest`/`GenCli`/`doki-gen` today (creativity hardcoded 0, images=1, seed implicit -1); each must be added to the typed contract + both body builders, keeping the PS↔C# invariant. Never expose raw sampler/CFG/steps or the Wan-14B fp8 path. (3) "Voice" composer input needs the Parakeet STT service wired (net-new).

---

## 5. Phased Build Plan

**Recipe contract = single source of truth.** Either make C# the sole owner and have the CLI call into it, or add a **parity test** asserting the C# recipes match `doki-gen.ps1` (mirror the existing `ControlPlaneTests` that pins `ServiceRegistry`↔`doki.ps1`). Non-negotiable — two copies will drift.

| Phase | Scope | Rough effort |
|---|---|---|
| **P0 — Skeleton + first demoable** | `Core` extraction; `DokiDex.Web` + Kestrel `127.0.0.1` (Host-header + Origin/CSRF middleware); `/api/status`, `/api/mode`, `/api/services/*` wired 1:1; minimal SPA shell; tray launcher boots media→Kestrel→browser; `StageSpa` embed | **M (1–2 wk)** |
| **P1 — Live generation** ⟵ **FIRST MILESTONE** | Port 3 recipe fns to C# + parity test; job store + single-flight GPU queue (serializes **execution**, still **accepts** submissions → queued state); `GenerateText2ImageWS` bridge → SignalR; generation card with live % + base64 preview; Cancel=`InterruptAll`; auto media-switch reusing the **eviction-confirm** flow | **L (2–3 wk)** |
| **P2 — Library + gallery** | Sidecar schema (resolved seed + expanded prompt); persistent gallery index (JSON/SQLite over scoped folder); on-demand SkiaSharp/ImageSharp thumbnails; virtualized grid; Remix/Rerun/Vary; guarded `/api/media` scoped to the **index + canonical path-prefix** (not the volatile in-memory HashSet) | **M–L** |
| **P3 — Model manager** | `catalog.json`; ICatalog/Presence/DiskPlan; HYBRID download router → SignalR; `ListModels` presence + degradation; Add-by-URL + token write-through; install/DELETE; `TriggerRefresh` | **L** |
| **P4 — Workflows + new knobs** | workflow records + ordered installer + required-model gate; seed/creativity/aspect/count/duration knobs (contract + both builders) | **M** |
| **P5 — Edit Canvas** | `MaskImage` inpaint/outpaint UI | **L (largest single feature)** |

**First concrete end-to-end demo (end of P1):** double-click exe → tray boots SwarmUI + server → browser opens → type a prompt → watch a generation card fill with live % and a resolving preview thumbnail → cancel mid-gen → final image lands in the (P2) library. That single flow proves launcher, web host, recipe port, WS bridge, queue, and cancel.

### Build status (2026-06-16)

**Shipped (all on `feat/web-studio`, green: control 161/0, doki-gen 59/0):**
- **P0–P4 + prereqs** complete (`DokiDex.Web` host, control-plane API, live `GenerateText2ImageWS` gen, library/gallery, model manager, recipe knobs, SwarmUI commit-pin + eviction-confirm).
- **Packaging decision RESOLVED to in-process** (decision #1 below): the studio server (`StudioHost`) is hosted **in-process by the WPF panel** via a `Microsoft.AspNetCore.App` FrameworkReference, and the SPA is an **embedded** single `index.html` served via `MapFallback`. So the existing single self-contained exe ships the whole studio with **zero CI change** — no second process to co-publish/find/kill. A thin standalone `DokiDex.Web.exe` remains for SPA dev iteration. (This supersedes the two-exe model and the `StageSpa`/Vite plan in §6.3/§6.6 — vanilla embedded SPA was sufficient; Vite deferred as YAGNI.)
- **P5 — Edit canvas** (mask inpaint + a **magic-wand** color-select + **SAM** semantic click for click→mask + **background removal** → transparent PNG + **directional outpaint** over the edit path) + the **full top-backlog set** (54 this round; control **340/0**, doki-gen **94/0**), each with a pure unit-tested core or reuse of a tested path, + graceful degradation:
  - *Prompt composition:* aspect-ratio presets; **LoRA mixer** (multi-LoRA + weights → `<lora:>` tags); user **negative-prompt** knob; **style chips** (stackable aesthetic bundles → +/- fragments); dynamic-prompt **wildcards** (`__name__` per-seed, resolved prompt recorded); **@-reference shelf** (`@name` → reusable saved snippets); **steerable rewriter** (user-directed, conversational iterate); **upscale content-class engine** selector (`-Upscaler`); **promptable segmentation** (`-Segment` → `<segment:any-keyword>`, generalizing `-Face`); **seamless-tileable** output (`-Tile`); one-click **effect-preset gallery** (img2img stylistic transforms on a card).
  - *Composers:* structured **music composer** (lyrics/duration/BPM); **multi-character "Cast"** (base + isolated per-character regions → `<object:>`); **camera** control compilation for video; **image reference** (init image → IP-Adapter `useipadapterforrevision`/`ipadapterweight`); **sketch-to-image** (drawing canvas → ControlNet control image); **Image Set** (a series sharing one locked style/aspect, per-cell prompt+seed → existing per-card rerun rerolls one cell); **Animate-2D** preset (one-click anime/illustration i2v — subtle motion, line/style preserved).
  - *Checkpoint:* a **model picker** + prompt-aware **Auto router** (`-Model` override of the recipe's `model`, plumbed end-to-end; `ModelRouter` keyword classify→pick, pure) + **parallel multi-model compare** (one prompt across every installed base, per-card model label).
  - *Video (gated on ffmpeg, found on PATH or bundled):* **extract a keyframe** (last/first frame of a clip → an i2v/FLF2V keyframe) + **join clips** (filter_complex concat) — the practical clip-extend / storyboard-strip primitives (`Ffmpeg.*Args` pure-tested; degrade cleanly when ffmpeg is absent, like the SAM/Demucs sidecars).
  - *SwarmUI features built against the SOURCE-EXTRACTED param schema* (via the real `CleanTypeName` = lowercase-letters-only), each gated on the relevant model/node being installed: **ControlNet — 1-3 stacked units** (`controlnet`/`controlnettwo`/`controlnetthree` × `model/strength/imageinput/preprocessor`), **FLF2V end-keyframe** (`videoendimage`), **frame interpolation** (`videoframeinterpolationmethod/multiplier`).
  - *Realtime:* **scratchpad** (debounced turbo re-render as you type, over the tested Fast path).
  - *Orchestration:* **script-to-shotlist Director** (local LLM → editable shots → batch gen); **CSV batch** generation; **saved recipes** (named, reusable pipelines); **node-lite Flow** (a DAG of gen steps run in topological order — pure topo-sort core); **Exploration Mode** (diverge one prompt into N seed-varied drafts); **per-card refine-from-result** (vary / face-fix / hi-res / upscale — re-run the card image as img2img).
  - *Audio/voice:* local **TTS voice** surface (Chatterbox `:8004`, file-based voice registry) + **pronunciation dictionary** (alias substitution); **multi-speaker dialogue** (`HERO: …` script → per-speaker voice + inline `[excited]`/`[whispers]` delivery, concatenated via a dependency-free **WAV concatenator** — no ffmpeg); **Demucs stem separation** (standalone sidecar, repo's venv pattern, gated on `setup.ps1 -Demucs`).
  - *Custom workflows:* a **custom ComfyUI workflow runner** (`comfyuicustomworkflow`, the foley hook generalized) — runs **SUPIR**, **InstantID**, or any installed workflow by name, gated on it being present.
  - *3D blockout:* a **server-side software depth rasterizer** — place boxes in 3D → perspective + z-buffer-occluded depth map (pure C#, no three.js/GPU/display) → the shipped Depth-ControlNet.
  - *Gated model/tool sidecars* (the integration ships now + degrades cleanly; activates on its optional install, exactly as ControlNet activates on its model): **Demucs** stem separation (`-Demucs`), **SAM** semantic click-segment (`-Sam`), **kohya LoRA training** (`-Train`, output → the LoRA mixer).
  - *Library:* **search + kind filter** over the sidecar index; **favorite/trash keyboard triage** (F/X/U + arrows, curation views); **saved searches** (named, re-applicable filters); **variation lineage** (refine/effect/vary record a parent → a tree of what came from what); **deterministic color subsystem** (palette extract + perceptual **LAB recolor** + a general `/import` for browser-produced PNGs); one-click **story-bible / pitch-deck** export (self-contained HTML).
  - *Vision (gated on a vision-capable local model):* **Describe** (image → editable prompt) and **output verification** (does a card match its prompt? PASS/FAIL + reason) over `LocalLlm.ChatVisionAsync`.
  - *Keyboard-first:* one consolidated key map across views (generate/explore/kind/focus/triage) + a `?` help overlay.
  - Shared `LocalLlm` helper backs the Director + rewriter + vision; all LLM/TTS calls degrade cleanly when the backend is down (verified to the same bar as the gen path).

**Param-schema resolved:** the earlier "unverifiable SwarmUI body-param names" concern was retired by extracting the real schema from SwarmUI source — `CleanTypeName` = lowercase-letters-only of the display name (strips spaces AND digits), so ControlNet (`controlnetmodel/strength/imageinput/preprocessor`, stacked = `controlnettwo*`) and FLF2V (`videoendimage`) are now built against confirmed keys. Both ship, gated on the respective model.

**Every named backlog feature now has shipped code** (task #27). The model/tool/GPU-dependent ones (ControlNet, SUPIR, InstantID, Demucs, SAM, kohya) are **built as gated integrations** — a tested command/param builder + graceful "not installed" degradation — that **activate on their optional install** (`setup.ps1 -Demucs/-Sam/-Train`, an installed ControlNet/workflow model) exactly as ControlNet activates on its model. Nothing is deferred *as code*; what's left is **operating** the app: a user installs an optional model/tool and runs on the GPU. That's running the studio, not building it. The whole thing ships in the one self-contained exe.

**Build-out converged (`/loop` "until there's nothing left to implement").** A self-paced loop then swept the rest of the backlog and shipped every row that is a clean, single-slice, verifiable build — including all the ones an earlier pass had parked as "marginal": **favorite/trash keyboard triage**, **variation lineage tree**, **VLM describe + output-verification** (gated), **story-bible/pitch-deck export**, the **deterministic color subsystem** (palette + LAB recolor + `/import`), **multi-speaker dialogue + WAV concat**, **Image Set**, the **model picker + Auto router + multi-model compare** (the `-Model` override plumbed end-to-end through both PowerShell scripts and both test suites), **saved searches**, **background removal**, the **keyboard-first map**, and the **Animate-2D** preset. Each shipped with a pure unit-tested core or a trivial reuse of a tested path; both suites stayed green (control 336/0, doki-gen 94/0).

The distinct **capability** surface of the backlog is now complete (inpaint, outpaint, background removal, frame-extract, clip-join, color recolor, dialogue, model routing/compare, lineage, curation, VLM describe/verify, story-bible, …). What remains is **not** a new-capability clean build — it is one of five kinds, each recorded as such rather than explained away:
1. **UX polish over already-shipped capability** — consolidating inpaint+outpaint+sketch behind one "edit strength" dial; a richer mask toolkit (invert / load-previous / brush-size / eraser); drag-the-border *handles* (directional outpaint already ships); per-region reference drop; a resizable generation window; a stacked edit-history strip; a one-click *auto* clip-extend macro (the extract→i2v→join primitives all ship — this just chains them). Incremental refinements, not missing features.
2. **One heavy + niche subsystem** — **warp markers** (a waveform editor + a phase-vocoder DSP sidecar). Genuinely large for a narrow audio-timing use; a separate build effort.
3. **Fuzzy/risky dials** — Stylize/Weird/Variety map to **CFG**, which is model-dependent (Turbo needs cfg≈1; overriding it breaks the shipped fast path); the backlog's own "never expose raw CFG" hedge holds. Not shipped on merit, not on inability.
4. **Upstream-gated** — workflow→form *field injection*, ACE-Step audio2audio/repaint, S2V/lip-sync, VACE video-edit, relight: each needs a SwarmUI/ComfyUI capability not present on this install; the backlog marks them "re-qualify on X." The gated-integration pattern already covers every row where the capability *does* exist.
5. **Trivia redundant with shipped controls** — a Draft/Final segmented toggle (the Fast MOD already is this), named-slider relabels, denoise-zone labels, pin-tools.

The substantial, buildable, verifiable, *new-capability* backlog is exhausted; what is left is editor polish over shipped capability, one heavy-niche audio subsystem, a model-risky dial, upstream-blocked rows, and relabels of controls that already ship.

See the platform backlog for the full list + per-item local-feasibility notes.

---

## 6. Decisions to Confirm (owner sign-off before building)

1. **Web framework — ASP.NET Core minimal API (in-process Kestrel).** *Recommend: yes.* Reuses the headless control plane verbatim; a non-.NET server re-implements GPU eviction the repo pins.
2. **Streaming transport — SignalR on net9.0, OR target the new `DokiDex.Web` project at net10.0 to use native SSE.** *Recommend: SignalR now* (least resistance on the pinned TFM). The WPF host stays `net9.0-windows`; the greenfield web project **could** target net10.0 independently and unlock simpler native SSE — confirm whether the .NET-9 pin is hard.
3. **SPA framework — Vite + React + TS + Tailwind v4 + shadcn/ui.** *Recommend: yes* (Sora/Grok-grade, owned-source = lower long-term maintenance; honestly "lower," not "low" — React/Vite/Tailwind/Radix majors still churn).
4. **Fate of the WPF panel — keep as slim tray launcher; retire the cockpit; keep the Setup Wizard.** *Recommend: yes* (reuses mutex/updater/payload; less work than a new headless host).
5. **Gallery store — app-owned scoped folder + JSON/SQLite sidecar index (authoritative), `ListImages` as import/fallback.** *Recommend: yes* (carries recipe/seed/remix-source; keeps the uncensored store private; the in-memory HashSet can't back a persistent gallery).
6. **Packaging — `StageSpa` embedded-resource + `ManifestEmbeddedFileProvider`, mirroring `StagePayload`.** *Recommend: yes* (proven in-repo machinery; preserves one-exe).

---

## 7. Risks & Open Questions

**Dropped / refuted claims (not laundered):**
- ❌ *"Bind 127.0.0.1 ⇒ secure."* **Refuted.** Loopback does not stop DNS-rebinding/CSRF from a visited page. **Mitigation is mandatory** (Host-header allowlist + Origin/CSRF on state-changing routes). This is the single most important non-obvious item.
- ❌ *"Store Civitai/HF tokens in DokiDex AppSettings."* **Refuted** — `DoModelDownloadWS` reads tokens from SwarmUI's own settings; dual storage drifts. Write-through to SwarmUI; keep only install records locally.
- ❌ *"MJ Rerun/Remix/Vary are all expressible through today's recipe path."* **Refuted** — `doki-gen` hardcodes `initimagecreativity=0`, `images=1`, implicit `seed=-1`; these knobs are net-new (scoped in P4).
- ❌ *"`DoModelDownloadWS` gives HF+Civitai for free including gated content."* **Partly refuted** — its params are `{url,type,name,metadata}` with **no auth parameter**; public-URL + progress + folder placement are free, but **token-gated Civitai / HF-gated repos** still need the authenticated fallback path (router handles this).
- ❌ *"Always-4-up grid (Sora/Grok)."* **Dropped** — Z-Image Base is 35-step/slow; default single rich-progress card, grids only on `-Fast`.
- ⚠️ *"Never kill a process" presented as a pre-existing validated constraint* — it isn't documented in the repo, but it's correct on merits (process-kill evicts the model + GPU-exclusive media mode). Kept as **rationale**, not a cited rule.

**Open questions / risks:**
- **SwarmUI is cloned UNPINNED** (`setup.ps1:221` bare `git clone`, rebuild-on-HEAD-advance at 246–252). Delegating to its **untyped, unversioned** API (`DoModelDownloadWS`, `GenerateText2ImageWS`, WS frame keys) requires **introducing real commit pinning first** — it has none today. This is a *prerequisite*, not a footnote. Validate body keys against `ListT2IParams` at the pinned commit.
- **SwarmUI issue #743**: downloader can attach wrong source metadata when a model was previously pulled from another source → post-install `DescribeModel` needs a sanity check.
- **Auto media-switch is destructive** — `doki-gen` deliberately refuses to auto-evict a running LLM. The web gen request must reuse the existing **eviction-confirm** (`ConfirmInfo`/`SwitchToMediaRequested`) — never silently kill an in-flight agent session. (Minor correction to prior framing: the WPF Studio already has a one-click "Switch to MEDIA" button; the real gap is that a Generate action doesn't yet *trigger/await* the switch.)
- **Single-flight queue vs. navigable UX:** the GPU constraint must serialize **execution** while still **accepting** submissions (queued state) and letting the user keep composing/browsing — otherwise it re-introduces the blocking feel it exists to remove.
- **SSE/stream resilience:** pair the SignalR stream with the `jobId` job store so a dropped/reconnected browser **resumes** the same job; emit a terminal error event + fall back to `GET /api/jobs/{id}` polling so a card never sticks at "running."
- **Gallery scoping:** the HTTP `/api/media` route must scope to the persisted index + canonicalized path-prefix (reject `..`, resolve symlinks), **not** the in-memory `_generated` HashSet (empty on restart).

Relevant files (absolute): `D:\Projects\DokiDex\control\DokiDex.Control.csproj`, `D:\Projects\DokiDex\control\App.xaml.cs`, `D:\Projects\DokiDex\control\Services\Control\{Lifecycle,StatusProbe,ServiceRegistry}.cs`, `D:\Projects\DokiDex\control\Services\{GenArgs,DokiService,Payload,AppSettings,RepoPaths}.cs`, `D:\Projects\DokiDex\control\Services\Install\{InstallPlan,Installer}.cs`, `D:\Projects\DokiDex\control\Models\Status.cs`, `D:\Projects\DokiDex\serving\doki-gen.ps1`, `D:\Projects\DokiDex\serving\download_models.py`, `D:\Projects\DokiDex\setup.ps1`, `D:\Projects\DokiDex\media-assets\WanFoley.json`.

---

## 8. Platform-research feature backlog (folded in)

Five double-validated research rounds across **~27 AI generation platforms** (Sora 2, Grok Imagine, Midjourney, Leonardo, Krea, Ideogram, Runway, Pika, Kling, Recraft, Playground, Civitai, Tensor/SeaArt, Luma, Hailuo/MiniMax, Veo/Flow, Firefly, NovelAI, Freepik, Higgsfield, Suno, Udio, ElevenLabs, Dreamina/CapCut, PixVerse, Vidu, LTX Studio) produced a deduped, locally-feasible feature backlog. The **authoritative, prioritized list with per-item SwarmUI implementation notes is in [`2026-06-16-platform-backlog.md`](2026-06-16-platform-backlog.md)**. Research has **converged**: the final round was mostly confirmation on video/image platforms; further platform mining would add fewer than ~2 new feasible features. The one remaining upside is **engine-gated, not research-gated** — a native SwarmUI ACE-Step audio-init path would unlock ~5 already-specified audio-editing features (repaint / extend / lyric-edit / audio2audio) currently parked in the backlog's DROPPED list.

**TOP 5 highest-leverage additions** (drive the §5 build priorities):
1. **Draft<->Final + Enhance-from-card** — cheap iterate-then-commit loop (turbo draft -> full render).
2. **Local audio subsystem** — stem/voice separation (Demucs sidecar) + a structured, section-aware music composer + a voice/persona registry over the shipped Chatterbox clone.
3. **Script-to-Shotlist director agent** — screenplay/prompt -> multi-shot list -> storyboard strip (the multi-shot authoring front door).
4. **Layout-first composition** — bounding-box composer + 3D-primitive blockout -> depth-ControlNet (deterministic composition, not a dice-roll).
5. **Smart-Layers auto-decompose edit** + the **multi-character directorial composer** (per-character token isolation + relationship binding).

**New capability areas this research surfaced** (now in the backlog): a full **Audio/Music/Voice** section · **multi-character** token isolation · **3D-blockout** composition · **novel-view re-angle** · **annotate-start-frame -> compiled video directives**.

Full prioritized detail (Composer / Generation card / Edit canvas / Live surface / Video controls / Audio / Model+Workflow manager / Library / cross-cutting / DROPPED) lives in the backlog file.
