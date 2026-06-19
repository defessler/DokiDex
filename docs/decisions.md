# Decision log

## 2026-06-19 — Chat surface built out P2→Pn + shipped (v0.7.0); anime SDXL pack added; remaining model-adds gated

Drove `feat/chat-phases` through the design's full phase plan via ultracode (per-phase
build + adversarial-review + fix workflows, TDD, each a verified green commit): **P2** SSE
token streaming (cancellable read bounded by the request token; in-band `event: error`
since you can't 503 after headers flush); **P3** lorebook-lite (keyword-triggered
`[World Info]` injection; `ChatPrompt.Build` + `ActivateLore` share one `RecentTurns`
window so activation scans exactly the turns the prompt sends); **P4** voice readback
(per-assistant-bubble TTS reuse of `/api/speak`); **P5** vision-in-chat (attach a Library
image — incl. edit/inpaint stills — → multimodal turn forced to the Vision tier via the
tested `Chat.VisionModel`); **Pn.1** tool-calling (a bounded 4-hop / 4-min agent loop +
ONE curated in-process tool `search_library`; `ParseToolCalls` synthesizes unique ids,
the echoed assistant turn carries `content:null`, the per-hop transcript shaping is pure +
tested). Web-search / code-RAG / generate-from-chat are deferred as future gated tools
(external integration / GPU handoff). Suite **376 → 452** green throughout; Debug+Release
clean each commit.

**Model-adds — shipped the one cleanly-wireable add:** the **anime SDXL pack**
(Illustrious-XL v1.0 + Animagine XL 4.0) as gated `-Models full` downloads + matching
`model-catalog.json` rows so they surface in the Studio picker (and via SwarmUI's native
picker + the `-Model` override; `ModelManager` resolves by filename). URLs HF-tree +
live-HEAD verified; an AST-driven `setup-helpers` test pins the entries. Self-contained
SDXL checkpoints route through the existing image recipe — no recipe/node work needed.
On-GPU load + image quality is the labeled remaining step (first full checkpoints in the
kit vs the existing DiT/unet models).

**Remaining model-adds = gated follow-ups (NOT blind-shipped, per verify-before-ship):**
FLUX.2 Klein, Qwen-Image (GGUF, needs the city96 ComfyUI-GGUF node + TE/VAE), Wan 2.2 A14B
GGUF (dual-expert high/low-noise recipe DokiDex doesn't have), PuLID-Flux + InfiniteTalk
(ComfyUI custom-node / sidecar integrations), TTS-Audio-Suite (a ComfyUI-node vs the
standalone Chatterbox `:8004` architecture decision), and Nunchaku NVFP4. Each needs
multi-component/node integration, a recipe tuning pass, or an architecture decision that
requires on-GPU verification — none a clean unit-testable slice. Full map in
`docs/superpowers/specs/2026-06-18-ai-platform-models-workflows-research.md`.

**Update — the music `xl_base` quality tier SHIPPED as a gated integration** (branch
`feat/model-adds`; moved off the gated-follow-ups list above). It turned out to be a clean,
unit-testable slice after all: an opt-in **`-Quality`** switch swaps the music default
(ACE-Step 1.5 turbo) → **ACE-Step 1.5 XL base** (50 steps / cfg 6 / euler / simple), with
the params **SOURCED from the official ComfyUI example template** `audio_ace_step1_5_xl_base.json`
(KSampler `[50, 6, "euler", "simple"]`) — so cfg=6 is sourced, not the unsourced guess the
prior note worried about. `xl_base` was already downloaded by `setup.ps1`, so no new asset;
the **turbo default is unchanged** (byte-for-byte) and `xl_base` is opt-in only. On-GPU
OUTPUT quality is the single labeled remaining confirm step.

Released as **v0.7.0** (`feat/chat-phases` → `main`).

## 2026-06-18 — Platform research (3 passes) → native chat surface shipped (Chat P0); ACE-Step 1.5 / Qwen-Image-Edit confirmed already-present

Three adversarial **deep-research** passes (~335 agents, ~8.1M tokens, 46 claims confirmed / 8 refuted vs
primary sources) mapped the AI-generation + chat landscape onto the 32GB SwarmUI + llama.cpp stack. Docs:
`docs/superpowers/specs/2026-06-18-ai-platform-models-workflows-research.md` (model/workflow map) and
`2026-06-18-chat-assistant-surface-design.md` (the chat design).

**Headline finding: the one real product gap is a conversational chat surface** — Open WebUI / LM Studio /
SillyTavern are all frontends over an OpenAI-compatible `/v1`, which DokiDex's llama-swap `:8080` already is,
so it's a frontend build, not an inference project.

**Shipped (branch `feat/web-studio`→`feat/chat-surface`): Chat P0** — a native persona-first chat view in the
DokiGen Studio SPA over `POST /api/chat`, built via an ultracode build+adversarial-review workflow, TDD. New:
`control/Web/{ChatPrompt,Persona,ChatStore,Chat}.cs` + `LocalLlm.ChatTurnsAsync` + `/api/chat` `/api/personas`
`/api/chats` endpoints + the SPA Chat view (persona Character Cards, on-disk threads, a symmetric GPU-arbitration
guard mirroring the media eviction-confirm, non-streaming). Pure `ChatPrompt.Build` + `Chat.SelectHistory` +
the store round-trips unit-tested; **suite 354→376 green, Debug+Release clean.** Streaming / lorebook / voice /
vision / tool-calling are later phases per the design doc.

**Verified-already-shipped (the research's "incumbent" labels were about the field, not DokiDex's real state):**
- **ACE-Step 1.5** is already the music engine — `setup.ps1:530-532` downloads `acestep_v1.5_xl_base_bf16` **and**
  `acestep_v1.5_turbo`; the recipe runs turbo (adopted 2026-06-14). No swap to do.
- **Qwen-Image-Edit-2511** is already the `edit` kind (`setup.ps1:523`). In-image text editing is just prompting.

**Recorded as gated follow-ups (NOT shipped blind, per the verify-before-ship discipline):**
- **Music quality tier** — the already-downloaded `acestep_v1.5_xl_base` is unused (recipe only loads turbo).
  Wiring it as a quality option needs an **on-GPU `steps`/`cfg` tuning pass** (research gives steps≈50 for base;
  cfg is unsourced) before it ships — exactly the blind-tuned-param edit the repo refuses elsewhere.
- **TTS-Audio-Suite** (15-engine ComfyUI node + RVC voice-conversion, incl. IndexTTS-2 duration/emotion + Higgs
  v3) — high-leverage, but a **ComfyUI-node path vs the current standalone Chatterbox `:8004` server**: an
  architecture decision, not a blind wire-in.
- **Model adds** (32GB-feasible, gated on optional install): FLUX.2 Klein (art-style), Wan 2.2 A14B GGUF
  (SwarmUI's recommended quality video), PuLID-Flux (face identity), InfiniteTalk (lip-sync), Illustrious-XL /
  Animagine XL 4.0 (anime), Nunchaku NVFP4 (speed). Z-Image **confirmed** the right photoreal + real-time base.

## 2026-06-16 — Control panel becomes an all-in-one **installer + manager** (repo-independent)

The downloaded exe is now the product, built from the repo by CI but **independent of any cloned repo at
runtime** — fixing the "status unavailable / must live in a repo" failure when run standalone.

**Hybrid engine** (chosen over a full C# rewrite or pure-script): native C# **control plane** (status poll +
up/down/start/stop) so the everyday path needs no pwsh, + **bundled PowerShell** for the heavy install
(setup.ps1, ~100 GB model downloads, SwarmUI) and gen. A `ServiceRegistry` mirrors doki.ps1's
`$Services`/`$Profiles`, drift-guarded by a test that parses doki.ps1.

**Home resolution:** `RepoPaths.Root` prefers a saved `InstallRoot` (`%LocalAppData%\dokidex\settings.json`),
else walks up to doki.ps1 (dev), else the exe dir — never a hardcoded path. First run opens a **Setup Wizard**
(fresh install OR adopt an existing folder); the status overlay gained a "Locate DokiDex folder" recovery.

**Embedded payload:** the csproj zips the runtime scripts/configs into the exe (`make-payload.ps1` →
`DokiDex.payload.zip`); a fresh install extracts them to the chosen home, and a self-update refreshes them once
per version (never an adopted repo, never the downloaded models). Heavy assets still download via setup.

**Setup Wizard:** one user-picked install folder, a component checklist → setup.ps1 flags, prereqs detected
with **one-click-each** winget install (git/python/uv/pwsh) + guidance for App Installer + GPU driver, live log.

Built in two phases (foundation + native control plane; then the installer). Verified: `doki test` green (PS 107,
Py 53, .NET 122) + a local single-file publish (66 MB) confirms the payload embeds in the release artifact.
Spec: `docs/superpowers/specs/2026-06-16-allinone-installer-manager-design.md`.

## 2026-06-16 — Project-move fallout fixed + **Z-Image Base is the new image default** + media-quality upgrades

Triggered by moving/renaming the project `DokiCode → DokiDex` and a "make quality the default" pass.
Investigated + implemented via multi-agent (ultracode) workflows with adversarial verification.

**Move-proofed the paths.** The rename left hardcoded `D:\Projects\DokiCode` in committed files → broke the
coder stack, the memory MCP, and the download helpers. `serving\llama-swap.yaml` is now a `__DOKI_ROOT__`
template that `start-serving.ps1` substitutes into `.run\llama-swap.generated.yaml` at launch (llama-swap v224
does **not** expand `${ENV}` inside `cmd`, so the macro/env route fails — substitute in PowerShell);
`setup.ps1` pins the memory-MCP `server.py` path in `crush.json` on deploy; `download_{models,fim}.py` derive
`MODELS_DIR` from `__file__`; `RepoPaths` resolves from `Environment.ProcessPath` (single-file-exe-safe), not a
hardcoded fallback. A future move can't rebreak these.

**SwarmUI theme — real fix.** The DokiGen Void theme never applied (even *before* the move) because
`setup.ps1`'s default-theme step matched `^Theme:`, but the key in `Settings.fds` is **tab-indented** under
`DefaultUser:` → never matched → the default silently stayed `modern_dark`. Anchored the regex to allow +
preserve leading whitespace. Apply with `setup.ps1 -Media`.

**Image default: Turbo → Z-Image Base — SUPERSEDES the 2026-06-15 "keep Turbo" call.** That entry kept Turbo as
a near-optimal *speed* default; this changes the goal to *quality-by-default* (explicit request). `doki gen`
(no flag) now uses **`z_image_bf16`, 35 steps, CFG 4.5, real negatives, dpmpp_2m/karras** — the non-distilled
quality ceiling (Turbo's DMD/guidance distillation locks CFG 1 and caps detail). **Turbo moved to `-Fast`**
(8 steps, CFG 1) for seconds-fast drafts. Heavier challengers (FLUX.1-Krea, Qwen-Image-2512) stay eval-gated,
not defaults (`docs/frontier-roadmap.md`); FLUX.2-dev stays out (VRAM-tight).

**Video tuning.** Wan 2.2 TI2V-5B (still the default) gained its missing tuned flow settings — **Sigma Shift 8
+ uni_pc/simple**; the I2V clip went 25→49 frames. 14B A14B stays **off** (fp8 dual-expert OOMs 32 GB in
StepSwap, per 2026-06-14); the only zero-OOM 14B route (GGUF Q4_K_M) is an eval-gated A/B in the roadmap.

**New `doki gen` / Studio modifiers (all opt-in, default-off):** `-Refine` (a real hi-res-fix — the old
`-Upscale` set `refinercontrolpercentage=0`, i.e. pure ESRGAN that regenerates **zero** detail; `-Refine` uses
0.35 + tiling), `-Face` (SwarmUI-native `<segment:face>` inpaint refine, no download), `-Realism` (appends
`<lora:Z-Image-Realism:0.7>`; `setup.ps1 -Models full` fetches HF `suayptalha/Z-Image-Turbo-Realism-LoRA`,
Apache-2.0, as `Z-Image-Realism.safetensors`).

**DokiGen Studio output location.** Generations now default **beside the launched exe** (`<exeDir>\DokiGen`, via
`Environment.ProcessPath`) with a **persisted folder picker** (`%LocalAppData%\dokidex\settings.json`) +
mandatory writable fallback (Pictures → temp). Was `%TEMP%\dokigen`.

**Verification:** full `doki test` green (PowerShell 100, Python 33+, .NET 100); recipe/contract + logic
(template substitution, crush rewrite, theme regex, LoRA URL HEAD-check) verified. **Live** media behaviour
(actual Base quality, theme render, llama-swap model load) confirms on a fresh `setup.ps1 -Media` at the new
location — the git-ignored heavy assets (`models/`, `serving/llama.cpp`, `media/`) did not travel with the move.

## 2026-06-15 — Media quality research → keep Z-Image Turbo + the current rewriter *(image default SUPERSEDED 2026-06-16 → Z-Image Base; see above. The rewriter conclusion still stands.)*

Chasing the "quality" goal, researched the two GPU-free quality levers for `doki gen` / DokiGen Studio:
the default image model and the `:8013` prompt-rewriter instruction. **Conclusion: both are already at/near
the local-32GB SOTA as of mid-2026 — no change warranted.** A useful negative result that prevents
misdirected effort (and an unverifiable blind edit to a tuned prompt).

**Image model — stay on Z-Image Turbo.** It ranks **#1 open-source** (≈#8 overall) on the Artificial
Analysis text-to-image leaderboard, 8 steps, ~1–2 min, fits ≤16GB. The mid-2026 "successors" are **heavier
and *not* better on photorealism** for our use:
- *Qwen-Image-2512* — 40GB+ model / 21GB Q8 GGUF, ~5–6 min; great in-image text, but a practitioner rundown
  rates it below Z-Image Turbo on photorealism. (We already use *Qwen-Image-Edit-2511* for the `edit` kind —
  that stays; it's the right tool there.)
- *FLUX.2 Dev* — 32B (+24B TE), 32GB Q8 GGUF (fills the whole card), 20+ min, "soft/blurred faces." Too heavy
  + slower + weaker photorealism. Ruled out.

**Rewriter instruction (`$mpInstr` in setup.ps1) — already Sora-2-shaped.** It does one-camera-move,
shot-size/lighting/light-direction/color/angle, style-first, locked subject ("keep subject+action exactly,
no new subjects"), and "avoid abstract mood words" — which is exactly current Sora-2 cinematic-prompt
guidance. Sora-2's third "audio" section does **not** apply: Wan/LTXV are silent and audio is a separate
Foley pass (`-Foley`). So no rewrite; editing it blindly (it can't be A/B-tested without the card) risks a
silent regression.

**Backlog (GPU-eval only, low priority):**
- *FLUX.2 Klein* (4B, distilled from FLUX.2 32B, "real-time on consumer HW") — speculative: FLUX-family
  *prompt adherence* could help complex compositions even if photorealism ≈ Z-Image. Not in the practitioner
  rundown yet → unverified. Run through the eval gate if/when curious. See `memory: dokidex-model-eval-candidates`.
- *Optional rewriter A/B deltas* (test on-card before adopting, don't ship blind): plural "3–5 color anchors"
  vs the current single "color tone"; an explicit "avoid pronouns — reuse one locked subject descriptor."

Sources: [Z-Image (Tongyi-MAI)](https://github.com/Tongyi-MAI/Z-Image) ·
[Diffusion Doodles model rundown (Z-Image Turbo / Qwen-Image-2512 / FLUX.2 Dev)](https://medium.com/diffusion-doodles/model-rundown-z-image-turbo-qwen-image-2512-edit-2511-flux-2-dev-fc787f5e87ad) ·
[Sora 2 prompting guide (WaveSpeed)](https://wavespeed.ai/blog/posts/sora-2-prompting-tips-better-videos-2026/) ·
[Best open-source image models 2026 (WaveSpeed)](https://wavespeed.ai/landing/models/best-open-source-image-models-2026)

## 2026-06-12 — Phase 2: harness bake-off → **Crush** is the daily driver

8-run matrix (2 harnesses × 2 models × 2 golden tasks), headless via `evals/run-eval.ps1`, objective pass checks. Clean results after fixing a check bug (array `-notmatch` false-negative):

| Combo | t1 feature (+test) | t2 bugfix |
|---|---|---|
| Crush × coder-fast | ✅ 53.7s | ✅ 18.9s |
| OpenCode × coder-fast | ✅ 41.4s (1 no-op flake before it) | ✅ 12.4s |
| Crush × coder-big | ✅ 120.7s | ✅ 120.4s |
| OpenCode × coder-big | ✅ 103.1s | ✅ 67.7s |

**Both harnesses work well with both local models — 8/8 task success on clean runs.**

### Why Crush wins as default

1. **Zero flakes** in the matrix; OpenCode hit one no-op run (model answered without tool calls; opencode run ended silently after 3.3s). Small sample, but consistent with Crush's tighter prompting of open models.
2. **Windows-native Go binary** — no Node runtime, instant startup, no npm shim issues (OpenCode's `.cmd` shim breaks on `<`/`>` in prompts when invoked via cmd; we bypass with the native exe in the runner).
3. **Scoped permission model that fits our eval design**: per-workspace `crush.json` `permissions.allowed_tools` enables headless runs in sandboxes while the global config keeps interactive ask-mode.

**OpenCode stays installed as challenger** — it was consistently slightly *faster* on tasks, its explore-subagent behavior is genuinely good, and the eval suite can re-judge any time (`run-eval.ps1 -Harness opencode`).

### Permission findings (important)

- **Interactive** Crush prompts before tool use by default (no `allowed_tools` in our global config; `--yolo` exists to bypass — i.e., prompting is the default).
- **Headless `crush run` auto-executes tools** — measured: it deleted a file when asked, with NO allowlist configured. Same class of behavior in `opencode run`.
- **Rule: never point headless `run` at a repo you care about without committed git state.** Eval runs always use throwaway copies (runner enforces this).

### Harness invocation gotchas (encoded in run-eval.ps1)

- `crush run`/`opencode run` block forever on a never-closing stdin pipe → always redirect stdin from an empty file in automation.
- Pass prompts after a `--` terminator: `Start-Process` joins args unquoted, so `--reverse` inside a prompt parses as a flag otherwise.
- Use OpenCode's native exe (`node_modules\opencode-ai\bin\opencode.exe`), not the npm `.cmd` shim.

### Model observations (small-n, watch in Phase 3)

- coder-fast ≈ 2–5× faster wall-clock than coder-big on identical tasks; both solved everything here.
- coder-big's wins should show on harder/longer tasks; t1/t2 are too easy to separate them.

## 2026-06-12 — Phase 4: web search = keyless DuckDuckGo MCP

- **`duckduckgo-mcp-server` via `uvx`** (no API key, stdio, actively maintained). SearXNG (self-hosted, Docker) is the documented upgrade path if DDG result quality limits us — not needed yet.
- Wired as `mcp.websearch` in the Crush config. Kept it the **only** MCP server — open models lose tool-selection accuracy as the tool list grows (TDD §6.2).

## 2026-06-12 — Phase 5: FIM model = Qwen2.5-Coder-3B (not 7B)

- TDD suggested a 7B FIM model, but at 32GB VRAM the **3B Q8** (~3GB) is the better pick: it coexists with the agent model and FIM latency favors smaller. Quality at 3B for fill-in-the-middle is excellent (292 tok/s, correct completions).
- **Coexistence solved with `coder-fast-lite`** (coder-fast at 64k ctx, ~21GB) instead of a tinier FIM model: 64k is plenty of agent context, and 21GB + 6.6GB FIM = ~27.6GB fits with margin. Full-128k coder-fast (30GB) cannot run alongside FIM.
- FIM served on a **dedicated `:8012`** rather than a llama-swap group — keeps autocomplete always-on and independent of which agent model is swapped in.

## 2026-06-13 — Harness watchlist: Claw Code wired in as a third eval challenger

Added **`claw`** as a `-Harness` option in `run-eval.ps1` / `run-suite.ps1` so
Claw Code can be bake-off'd against Crush/OpenCode on the same 11-task suite.
**Status: evaluating, not adopted** — Crush stays the daily driver until claw
beats the 91% scorecard with zero tool-call flakes on our local models.

Why it cleared the bar to even get wired in (vs. OpenClaw, which is a
personal-assistant/chat gateway, not a coding harness, and was rejected):

- **Pure-Rust `claw.exe`, no Node** — the `codetwentyfive/claw-code-local` fork
  of `ultraworkers/claw-code-parity` (a clean-room Claude Code harness reimpl).
  Matches Crush's Windows-native, no-runtime advantage.
- **Drives the local endpoint** — with `OPENAI_API_KEY` set and no Anthropic
  auth, `detect_provider_kind()` routes a non-claude/grok model name to the
  OpenAI-compatible client → llama-swap (`rust/crates/api/src/providers/mod.rs`).
- **Headless one-shot**: `claw --model <m> --permission-mode danger-full-access
  prompt -- "<task>"`. Default permission mode is already danger-full-access
  (auto-executes tools) — same posture as headless crush/opencode `run`; safe
  only on the throwaway eval workspaces.
- **MCP / LSP / 3 permission modes / 19 gated tools** on paper (README parity).

Runner integration details (`run-eval.ps1` claw branch):
- Mirrors the seed's `AGENTS.md` → `CLAUDE.md` per run, since claw reads
  CLAUDE.md — keeps conventions identical across harnesses (fair bake-off).
- Sets `OPENAI_BASE_URL`/`OPENAI_API_KEY`, clears `ANTHROPIC_API_KEY` for the
  child (restored after) so routing can't fall back to Anthropic. Assumes no
  saved `claw login` OAuth token on the machine.
- Exe resolution: `$env:CLAW_EXE` → `%LOCALAPPDATA%\claw-code-local\…\claw.exe`
  → `claw` on PATH.

**Prerequisite to run it:** the binary must be built from source — no prebuilt
Windows release exists for any claw-code repo, and Rust isn't installed here yet.
`evals\build-claw.ps1` clones + `cargo build`s it (needs rustup; the
`stable-x86_64-pc-windows-gnu` toolchain avoids the VS C++ build-tools
dependency). Scorecard is pending that build.

Caveats on the radar: ~3-month-old project, fragmented fork namespace
(`instructkr/claw-code` vs the parity reimpl vs this local fork), inflated
star/fork metrics. Per our philosophy: watch, measure, don't switch on hype.

### Result (2026-06-13): 5/11 (45%) — flaky tool calls; Crush (91%) keeps the slot

Built `claw.exe` (Rust 1.96 MSVC; ring/rustls compiled clean in ~32s) and ran the
full suite → `docs/scorecards/2026-06-13-claw-coder-fast.md`.

| Harness × coder-fast | Score |
|---|---|
| Crush | 10/11 (91%) |
| **claw** | **5/11 (45%)** |

Passed (real edits landed, so tool execution genuinely works): t1, t3, t6, t7, t10.
Failed: t2/t4/t5/t11 (edited but wrong fix / broke build), t8 (1.1s no-op),
t9 (no file written).

Root cause = **tool-call flakiness**, not a hard break. claw sends proper OpenAI
`tools` (`openai_compat.rs:664`), but its Claude-Code-derived prompt also elicits
a *textual* `<function=…>` format from Qwen3-Coder that intermittently isn't
executed — the call leaks through as content and no edit happens. Proven flaky:
**t9 failed in the suite but PASSED on an identical re-run** (10.4s, file created),
so the true rate is ~45–55% with run-to-run variance.

That fails the precise criterion Crush won Phase 2 on — *zero flakes* — and a
correctness gap on the harder bugfixes compounds it. **Verdict: not adopted;**
Crush stays the daily driver. Worth a re-judge if the fork adds robust native
`tool_calls` parsing for OpenAI endpoints, or against a model whose tool format
matches claw's parser. Runner support stays in for one-command re-tests
(`run-suite.ps1 -Harness claw -Model coder-fast`).

(Correction: a single initial smoke test wrongly implied claw couldn't execute
tools at all; the suite corrected that to 45% — exactly why we never judge on
one sample.)

## 2026-06-13 — Media generation: local image + video, fully headless

Added a fully-local, unrestricted image + video capability + a one-command installer
(`setup.ps1 -Media`). Tool: **SwarmUI** (friendly UI on the ComfyUI engine); the
ComfyUI backend is provisioned **100% headlessly** via the `InstallConfirmWS`
WebSocket (no GUI wizard). Runs as the `doki up media` GPU-exclusive mode on `:7801`.

Verified live (2026-06-13):
- **Image — Z-Image Turbo** (uncensored, arch `z-image`): coherent photoreal 1024²
  in **54s** first call (incl. auto encoder/VAE download), ~seconds after.
- **Video — Wan 2.1 1.3B** (uncensored): coherent 832×480 16fps 1.56s clip in **25s**.

Reliability finding → default model choice: **Wan 2.1 14B** at 832×480×25f maxes the
32GB card (~700MB free → VAE-tiling/offload thrash, 20+ min/clip; had to hard-kill
ComfyUI to recover). So the **reliable default is Wan 1.3B** (~22GB headroom,
~25s/clip); 14B + the Lightx2v 4-step LoRA are a `-Models full` quality opt-in (keep
res/frames modest). Image default: Z-Image Turbo.

Unrestricted: SwarmUI/ComfyUI apply no content filter and the base models are
uncensored; nothing leaves the machine. Only hard limits are legal (no CSAM; no
non-consensual real-person sexual content). Media is GPU-exclusive with the LLM on
32GB — `doki` enforces the mutual exclusion.

Follow-up: **Wan 14B is reliable after all — at a VRAM-safe config.** 480×320 / 17
frames / 4-step Lightx2v LoRA generates in **~87s** (verified) and is visibly higher
quality than 1.3B. So the rule is res/frames, not the model: 1.3B for speed (≤832×480),
14B-small for quality. Docs: `docs/wiki/8-image-and-video.md`.

## 2026-06-13 — Model refresh: Nemotron-Cascade-2 eval'd → incumbent Qwen3-Coder retained

First eval-gated coder refresh (per `workflow.md` cadence). HF-verification first:
the listicle's "Qwen3.6-35B-A3B" **doesn't exist** (invented); the real fits-32GB,
swap-compatible candidate is **Nemotron-Cascade-2-30B-A3B** (NVIDIA) — same 30B/3B-active
class. Wired as `coder-candidate` (q4_k_m 22GB, 64k ctx), smoke-tested, run through the suite.

| crush × model | tool-calls | decode | golden suite |
|---|---|---|---|
| coder-fast (Qwen3-Coder-30B) | clean | 265 tok/s | **10/11 (91%)** |
| coder-candidate (Nemotron-Cascade-2) | clean | **320 tok/s** | **5/11 (45%)** |

Nemotron is ~20% faster and tool-calls cleanly, but is much weaker at correct edits —
it failed even simple tasks the incumbent passes (t1-reverse-flag, t2-slugify, t3-kelvin).
It's a reasoning/competitive-programming tune, not an agentic-coding tune. **Verdict:
no swap — Qwen3-Coder-30B stays the daily driver.** Scorecard:
`docs/scorecards/2026-06-13-crush-coder-candidate.md`.

The refresh *process* is now validated end-to-end: HF-verify → wire `coder-candidate`
→ tool-call smoke → `run-suite` → compare → swap-only-on-win → (on loss) revert config +
delete the GGUF. Image/video: Wan 2.7 (MoE 27B/14B) and Flux.2 exist but are heavier
than the current picks with no 32GB win, so not pursued. The incumbents ARE the most
reliable available — confirmed by measurement, not vibes.

### Candidate #2 (2026-06-13): Qwen3-Coder-Next-REAP-48B-A3B → rejected at smoke test

Tried the incumbent's successor line (the most-likely-to-win candidate). Qwen3-Coder-Next
is fittable on 32GB only as the **REAP-pruned 48B-A3B** (q4_k_m 27.6GB, ran at
`--n-cpu-moe 10` to fit). It **loaded fine but failed the tool-call smoke test** — refused
the provided tool and answered in plain text. REAP expert-pruning evidently damaged its
tool-use; that's a hard disqualifier for an agentic harness, so no point running the full
suite. No swap; GGUF deleted.

**Refresh concluded — no 32GB upgrade exists.** Two best candidates beaten: Nemotron
(clean tool-calls, but 45% correctness) and Qwen3-Coder-Next-REAP (broken tool-calls). The
non-pruned Next is 80B (RAM-offload / coder-big tier, not a coder-fast replacement).
**Qwen3-Coder-30B-A3B stays the daily driver** — purpose-built for agentic coding and the
best 30B-class fit, now confirmed against the field. The refresh *process* is proven and
repeatable; re-run it when a genuinely new ~30B agentic-coder GGUF appears.

### LLM re-verification + speed/quality tiers + vision fill (2026-06-17)

DeepSeek evaluation (two adversarial research passes, hardware pinned: RTX 5090 32GB +
64GB DDR5 + i9-14900KS). **Verdict: do not adopt DeepSeek.** The decode-speed law under
`n-cpu-moe` offload (tok/s ∝ 1/active-params — the same law that makes gpt-oss-120b's 5.1B
active viable) kills it: V4-Flash (13B active) ≈ 3–5 tok/s on an *unmerged* experimental
llama.cpp fork; the 671B/V4-Pro flagships are <2 tok/s / datacenter-only (RAM+VRAM caps at
~96GB < the 130GB+ smallest quant — hours per answer even with time relaxed); the fitting
distills regress tool-calls (the disqualifier class) or are degraded/2024-era; censorship is
baked into the weights. Re-evaluate only if V4-Flash merges upstream **and** measures ≥8 tok/s
on a single 32GB+DDR5 box with clean tool-calls.

**Correction to the 2026-06-13 refresh:** the note that "Qwen3.6-35B-A3B doesn't exist
(invented)" is now **stale** — Qwen3.6-35B-A3B shipped 2026-04-16 (Apache-2.0, public GGUFs),
as did Qwen3.6-27B (dense) and Qwen3-Coder-Next-80B-A3B (the FULL model — only its damaged
REAP-48B prune was ever tested here). The repo's own re-judge trigger ("a genuinely new ~30B
agentic-coder GGUF") has fired, and the pinned llama.cpp b9616 (2026-06-15) already supports
the qwen35moe / GLM-MoE / Qwen3-VL archs, so they're servable with no upgrade. These are
**bake-off candidates, not adoptions** — wired commented in `serving/llama-swap.yaml` +
downloadable via `setup.ps1 -LlmCandidates`; gate via `serving/test-toolcall.ps1` +
`evals/run-suite.ps1` (≥91% golden AND zero tool-call flakes) before any swap.

**Shipped this round (the user's "choose how fast" requirement):** a per-request **speed/quality
tier selector** on the latency-tolerant LLM workflows (Director, pitch-deck, multi-character
phrasing) — `LlmTiers.Resolve` maps Fast→`coder-fast` / Quality→`coder-big`, `LocalLlm` sends the
OpenAI `model` field so llama-swap loads it; rewriter (:8013) + FIM (:8012) stay untiered. **Vision
gap filled:** a gated `vision` block (Qwen3-VL-8B-Instruct + mmproj, `setup.ps1 -Vision`) lights up
the already-built Describe/Verify surfaces with zero studio code change (uncensored fallback:
abliterated Qwen2.5-VL on the same path). FIM + heavy + rewriter incumbents confirmed best-for-
hardware (rewriter's tuned instruction is the load-bearing part; Qwen3-4B-2507 is an optional
marginal swap, not taken). Full analysis: `docs/superpowers/specs/2026-06-17-deepseek-eval-llm-tiers.md`.

### Image refresh (2026-06-13): Chroma added as a 2nd uncensored image style

Added **Chroma** (silveroxides Chroma1-HD-fp8, FLUX-derived, "Censored? No") alongside
Z-Image Turbo. Gotcha caught the hard way: the repo's *largest* file lives in a
`do_not_use/` folder and errors in ComfyUI (tensor-size mismatch) — the stable file is
**`Chroma1-HD-fp8mixed-final.safetensors`** (auto-detects as `arch=chroma`). Verified:
coherent 1024² in ~179s (CFG 3.5, 26 steps) — slower than Z-Image (54s) but a genuinely
different softer/filmic look. **Kept as a 2nd option** (`-Models full`); Z-Image stays the
fast default. `setup.ps1` fixed to fetch the stable variant directly (the regex resolver
would have grabbed a `do_not_use/` file).

### Media upgrade (2026-06-14): Sora-2-from-simple-prompts — Wan 2.2 + a local prompt-rewriter + Foley audio

Goal: get local image+video **as close to Sora 2 as possible from SIMPLE prompts with little
tuning**. Decided after **three multi-agent research rounds**, every load-bearing fact verified
against primary sources (official repos + the HF file-tree API, with byte sizes and live URL
checks). Eval-gated as always: the lean floor (Z-Image Turbo + Wan 2.1 1.3B) is untouched, and
every swap is gated behind a blind simple-prompt bake-off.

- **The centerpiece is NOT a model — it's an always-on local LLM prompt-rewriter.** A tiny
  `Qwen2.5-3B-Instruct` (Q5, ~2.5GB) runs on its own port **:8013** (new
  `serving\start-prompt-rewriter.ps1`, in doki's **`media` group** so it coexists with the video
  model, ~24GB peak < 32GB). SwarmUI's **MagicPrompt** extension calls it; the user types
  `<mpprompt:lazy idea>` and the 3B silently expands it into the 60–120-word cinematic prompt the
  models were trained on. This is what manufactures "Sora-like from one sentence." The official
  Wan content-safety rewrite rules (8–10, which silently swap "sensitive" prompts) are **omitted**
  to honor the uncensored requirement.
- **Image: no swap.** Z-Image Turbo already beats FLUX.2 Dev / Qwen-Image / HiDream / FLUX.1 on
  photoreal-from-short-prompts across 3 independent 2026 reviews → no measured win. Added
  **Z-Image Base** (non-distilled, `Comfy-Org/z_image` `z_image_bf16`) as the "quality" ceiling
  (full CFG/negatives); Turbo stays the default; Chroma stays the stylized complement.
- **Video: Wan 2.2 TI2V-5B** (single 5B model, efficient VAE) is the reliable quality default —
  **validated live: 832×480×49 frames in 53s at 13.8GB VRAM**, a genuine upgrade over the 1.3B floor
  (and leaves ~18GB free, so the rewriter coexists trivially). **Live-test correction to the
  research:** the **A14B dual-expert** (fp8 high+low StepSwap) does NOT fit 32GB — both 13.3GB
  experts + the 6.3GB text-encoder + activations thrash past 32GB even at 480×320×33 (timed out
  >300s, VRAM pegged at 32GB) — the same wall decisions.md found for Wan 2.1 14B, made worse by the
  two-expert architecture. The research's "fits 32GB" claim didn't survive contact with the card.
  So the 14B is kept on disk but **NOT wired as the default**. **Block-swap was then tested live**
  (user opted to try it): Kijai's **WanVideoWrapper** imports clean on Blackwell (110 nodes, the
  experts have 40 swappable blocks), so it's *feasible* — but it needs its own model ecosystem (the
  fp8-scaled umt5 is unsupported → another ~11GB T5 download) + a hand-built dual-expert workflow,
  for a *slow* RAM-offloaded 14B with only a modest edge over the 5B. Poor ROI, so **5B is confirmed
  the practical max** on this 32GB card (user's call; WanVideoWrapper removed). GGUF-Q4 (~9.6GB/
  expert, native SwarmUI path) remains the cleaner option if the 14B is ever revisited. **Also
  corrected:** Wan 2.5/2.6/2.7 are API-only — Wan 2.2 is the newest OPEN Wan (verified vs the
  official `Wan-AI` HF org; "downloadable Wan 2.7" pages are SEO).
- **Audio: HunyuanVideo-Foley** (SOTA V2A, beats MMAudio/ThinkSound). Runs as a ComfyUI post-step
  on the silent clip; SwarmUI's own `SwarmSaveAnimationWS` node muxes the audio → **one muxed MP4
  from a single API call** via a committed `WanFoley` custom workflow. License: Tencent Hunyuan
  Community (local/personal OK; restricted in EU/UK/SK).

**Four errors caught by the "iterate until solid" discipline** (cross-checking agents against
primary sources): the Wan-2.7-is-open claim (false), the MagicPrompt tag form (it's the colon
`<mpprompt:...>`, not a paired tag), the Z-Image Base packaging source (`Comfy-Org/z_image`, not
`mcmonkey/swarm-models`), and a repeated "Wan 2.6 open weights" claim (false).

**Honest Sora-2 gap** (Sora 2 is itself deprecated — OpenAI API sunset 2026-09-24): stills
match/exceed Sora-2 keyframes; short 5s 480–720p Wan 2.2 clips are cinematic with a modest visual
gap; still short on 1080p+/10–25s length, hardest physics + multi-shot identity, and joint-trained
synced audio (Foley is good V2A post-hoc, not lip-sync). Speed/cost/control/uncensored win outright.

Design + verified URLs/wiring: `~/.claude/plans/generic-honking-turing.md`. Bake-off scorecards to
land under `docs/scorecards/` once candidates are generated head-to-head.

### Speech/TTS (2026-06-14): uncensored local text-to-speech + voice cloning

Added a fourth media modality — **speech**. Researched the mid-2026 open-TTS field (Chatterbox,
Kokoro, F5, Fish, Higgs, Orpheus, VibeVoice, XTTS, Dia, IndexTTS-2, Kani, …) against the
priorities: uncensored, zero-shot voice cloning, OpenAI-compatible serving, native Windows +
Blackwell, small enough to coexist with the coder.

- **Pick: Chatterbox (Resemble AI) behind the `devnen/Chatterbox-TTS-Server`.** The only candidate
  that hits *every* axis: best-in-class zero-shot cloning + emotion control, a real OpenAI
  `/v1/audio/speech` endpoint (+ `/tts`+`/upload_reference` for cloning), MIT license, and the
  best-supported native-Windows-Blackwell path (torch 2.9 cu128, SDPA — *avoids* the flash-attn
  sm_120 wall that kills Higgs/Orpheus). Kokoro-FastAPI is the no-clone fallback (Apache, tiny).
- **Uncensored:** none of the open models have an input content-filter or a working consent-gate;
  the only "safety" is watermarks. Chatterbox embeds a Perth watermark via one unguarded line in
  each model file — **stripped** (`apply_watermark(...)` → `wav`) in `tts.py`/`mtl_tts.py`/
  `tts_turbo.py`/`vc.py` so output is genuinely unmarked. (The binding constraint across the field
  is *licensing*, not censorship — Chatterbox's MIT is clean.)
- **Integration:** new `serving\start-tts.ps1` runs the server (its own isolated cu128 venv) on
  **:8004**; doki `tts` service in the **`llm` group**, in the `agent` profile. **Validated live:**
  synth (OpenAI route, 24kHz wav) + voice clone (from a reference wav) both work, and it
  **coexists with coder-fast at 30.6GB < 32GB** — so no GPU-exclusive mode, TTS rides along with
  chat/code. Works with Chatbox (point it at `http://127.0.0.1:8004/v1`).
- **Two live-debug fixes** (now baked into `setup.ps1`): the server's default `repo_id:
  chatterbox-turbo` is a **gated** HF repo (403) → switched to the public **original** `chatterbox`
  model; and the cu128 requirements pin `protobuf 3.19.6` but `onnx` needs ≥3.20 (`builder`) →
  pin **protobuf 4.25.5** (the descript-audiotools <3.20 pin is over-strict; synth works at runtime).

---

## 2026-06-14 — Control panel + frontier-gap media kit (I2V, music, image-edit, upscale, STT)

Two design workflows (control-panel stack judge; frontier-gap roadmap) → a unified plan, then
built + **live-verified end-to-end (`doki verify` 16/16)**. Specs: `docs/control-panel-design.md`,
`docs/frontier-roadmap.md`.

- **Control panel = C# WPF (.NET 9) + CommunityToolkit.Mvvm**, a thin reactive face over
  `doki status json` (enriched with a GPU gauge, pid, model menu, ui/vram). Chosen over PowerShell-WPF
  (single-STA runspace tax) and a web stack (extra server/port) because the .NET SDK + WindowsDesktop
  runtime were already on the box. Shipped on pure WPF + a ported VS-Code-dark theme (dropped the
  WPF-UI dep to remove the one offline-restore risk). Grouped LLM/MEDIA cards, GPU trust-meter, mode
  switcher with 32 GB-headroom + eviction confirm, live file-tailed logs, per-modality ⚡test, ghost
  cards for not-installed services. Launch via `doki panel` / `control.bat`.
- **Image-to-Video:** live testing **corrected the plan** — SwarmUI's *native* `videomodel` pipeline
  animates the installed Wan 2.2 ti2v-5B with **no custom workflow** (the `videosteps`/`videocfg`/
  `videoresolution` params are what make the I2V step fire). The hand-authored `WanI2V.json` was
  deleted: `SwarmInputImage` nodes need editor-generated `custom_params` and `${initimage}` yields a
  data-URL that `SwarmLoadImageB64`'s raw b64decode corrupts.
- **Music:** ACE-Step **1.5** is **SwarmUI-native** (class `ace-step-1_5`; qwen ace15 TEs auto-download)
  — NOT the v1 all-in-one I first HEAD-checked. XL base (quality) + turbo (fast). Verified 48 kHz MP3.
- **Image-editing:** Qwen-Image-Edit-**2511** ships `fp8mixed` (~20 GB), not `fp8_e4m3fn` — HF-tree
  verified. SwarmUI-native (class `qwen-image-edit-plus`): model + init image + instruction. Verified.
- **Upscaler:** 4×-UltraSharp fires only via the Refiner-Upscale group (`refinermethod=PostApply` +
  `refinercontrolpercentage=0` = pure upscale). Verified 512→1024.
- **STT:** Parakeet via onnx-asr FastAPI on `:8005` (own venv, CPU EP), OpenAI `/v1/audio/transcriptions`,
  `group=llm` so it coexists in agent mode. Live-debug fix: the route's `model` Form param shadowed the
  module-level `model()` loader → aliased it. Verified a TTS→STT round-trip.
- **VAE correction:** the Wan 2.2 5B uses `wan2.2_vae` (not `wan_2.1_vae` — that's the 1.3B floor's);
  `setup.ps1` comment fixed.

## 2026-06-14 — Installer + control-plane hardening (ultracode audit → fixes → regression tests)

An adversarial multi-agent code audit (24 agents) themed *"trust real readiness, not proxy
signals"* found **4 real bugs, all in `setup.ps1`'s fresh-install / failure paths**. Fixed all four,
then scaffolded the project's first PowerShell test layer so they can't regress. Net: **81 unit
assertions** across `doki test` (16 installer-helper + 41 status-json contract + 24 panel xUnit).
  *(The panel xUnit later grew to **41 / ~118 total** — see the auto-updater entry below.)*

- **PATH refresh (HIGH):** `Ensure-WinGet` never re-read PATH after a winget install, so a freshly
  installed python/git/dotnet wasn't found in the same run → `CommandNotFoundException` aborted setup
  under `$ErrorActionPreference='Stop'`. Added a `Sync-Path` helper (machine+user registry → `$env:Path`)
  invoked after every real install.
- **Guarded `dotnet` probe (HIGH):** `(dotnet --list-sdks 2>$null) -match '8\.0\.'` *threw* when dotnet
  was absent — the exact case it handled — because `2>$null` can't suppress a PowerShell
  CommandNotFoundException (line-20's `$PSNativeCommandUseErrorActionPreference=$false` only governs
  native exit codes). Now `Get-Command dotnet`-gated.
- **Verified-readiness venv gate (MED):** TTS/STT venvs gated on `.venv\Scripts\python.exe`, which
  `python -m venv` creates *first* — a failed pip left a broken venv that re-runs skipped as "present".
  Now gate on a `.deps-ok` sentinel written only after all deps succeed, with a `Pip` helper that fails
  loud on `$LASTEXITCODE` (line 20 otherwise mutes pip failures). Existing venvs were sentinel-backfilled.
- **Atomic model download (MED):** `curl -o $dest` left truncated files on interruption that the
  existence-only gate then trusted. Now downloads to `$dest.part` and `Move-Item`s on success.
  **Caught while writing the test:** the first draft used `-C - --remove-on-error` together — curl
  rejects that combo (*"mutually exclusive"*, exit 2), which would have failed **every** download.
  Dropped `--remove-on-error` (our own `.part` cleanup replaces it); kept `-C -` for resume. The test
  existed before the fix shipped, so the bug never reached a real run — the case for testing installers.
- **Status-json contract test:** the WPF panel parses `doki status json`; nothing guarded that seam.
  Added a test that runs the real command and asserts the schema + cross-consistency (every panel field
  present per service, groups ∈ {llm, media}, every profile names only real services). Catches the
  config-drift class of bug (profile typo, renamed/dropped field) that would break the panel silently.
- **Approach:** both PowerShell suites pull the *real* functions out of `setup.ps1`/`doki.ps1` by AST
  (by name) rather than duplicating logic — they exercise committed source and can't silently drift,
  and never run the install/switch bodies (no side effects).

## 2026-06-14 — Premium launch experience + app-wide re-theme ("THE SEAL IGNITES")

The console-flashing `control.bat` was replaced as the way to start the app, and the whole panel
re-skinned to one aesthetic. Both the design and the review were run as multi-agent (ultracode)
workflows: a 5-way **design judge-panel** (Iron Man/JARVIS · Star Trek/LCARS · FF summon ·
Anthropic-minimal · a from-scratch fusion → 3 judges each → synthesis) picked the **fusion** concept
(90/100); a separate **adversarial review** workflow then hardened the implementation.

- **The concept:** a gold Final Fantasy summoning hexagram *is* the etched faceplate of an Iron Man
  arc-reactor; on ignition it fires a cyan packet across the void to boot a Star Trek **LCARS
  telemetry rail from REAL `doki status json`** (GPU, live services, loaded model). Seal → Power →
  Instrument, one circuit. ~3.15s, skippable, zero-black-gap dissolve into the panel.
- **The system (now app-wide):** ~70% deep **void** field; **ONE** light-emitting accent (reactor
  cyan `#35E0F0` = live/active); **gold** `#E8C77A` as *etched structure that never glows* (sigil,
  section gold, MEDIA group). Epic-as-restraint, the taste borrowed from Anthropic/OpenAI products.
  Functional **amber/red are deliberately KEPT** in the dashboard — telling you a service is *down*
  is the panel's job (the boot stays monochrome; the dashboard is where alarms live).
- **Launch:** an arc-reactor **app icon** (`make-icon.ps1`, GDI+ multi-res `.ico`), a native
  `<SplashScreen>` still shown *before* JIT (`make-splash.ps1`), and a **console-free `DokiDex.lnk`**
  straight to the WinExe (`make-shortcut.ps1`, auto-created by `control.bat`/`doki panel`). `.lnk` is
  machine-specific → gitignored.
- **WPF calls that bit:** `AllowsTransparency=True` is load-bearing — WPF *ignores* `Window.Opacity`
  without it, and the handoff cross-dissolve fades window opacity (trades ClearType for the dissolve,
  fine for a 3s splash). The master storyboard is begun from code (no controllable-Seek trap); rows
  reveal via `DataTrigger` (never animate a bound `Opacity`); the status probe is fired before pixels
  move and a master curtain timer always opens MainWindow even if every probe fails. Review fixes:
  cancel the pwsh probe + stop reveal timers at handoff (the one real leak); commit the required
  icon/splash assets while gitignoring the heavy capture PNGs (a fresh-clone build break).

## 2026-06-14 — Packaging + in-app auto-updater (mirrors D4Scanner, safety-hardened)

The control panel is now published as a **self-contained single-file Windows exe** on `v*` tags
(`.github/workflows/release.yml`) with an in-app updater (`control/Services/Updater.cs`), mirroring
the owner's D4Scanner pattern. A combined ultracode review found the updater architecture sound but
its **unhappy paths unsafe**; the 17 findings were fixed before any release tag.

- **In-place, not versioned-rename:** DokiDex's exe has a stable path inside the cloned repo
  (`RepoPaths` walks up to `doki.ps1`) and `DokiDex.lnk` points at it, so the update swaps the exe
  **in place** (same name) rather than D4Scanner's versioned-sibling model, which would have orphaned
  the shortcut and the repo walk.
- **Safe-swap order (the key fix):** copy the staged bytes to a `.new` *beside* the running exe
  (where the expensive, possibly **cross-volume** copy — exe on the repo drive, staging under
  `%LocalAppData%` — can fail harmlessly), **verify** it's a complete PE (`MZ` + size; truncated
  downloads are rejected at download via `Content-Length`), *then* same-volume renames
  (running image → `.old`, `.new` → exe) that can't fail mid-stream. The running exe is **never left
  missing**. The swap runs **off the UI thread** (the copy is tens of MB).
- **Guards:** the in-place swap is gated to a real `DokiDex*` apphost via `Updater.IsSelfUpdatableHost`
  on **both** the launch (`App.OnStartup`) and interactive paths, so it can never overwrite the shared
  `dotnet.exe` host under `dotnet run` (which would corrupt a user-writable SDK — the guard previously
  held only on the interactive path). `FindStagedUpdate` picks the highest tag and staging is pruned;
  apply-on-launch falls through to the normal boot if relaunch fails (never vanishes the app).
  `UpdaterTests` pin the swap success/reject/sweep, `IsNewer`/`TagFromAssetFile`, the highest-tag
  selection, and the host guard (**56 panel tests total**, was 41).
- **Trust model (stated honestly):** the download is authenticated only by **HTTPS to the owner's
  *private* GitHub repo**; the `MZ`+size check is corruption/truncation protection, **not** an
  authenticity check. There is no Authenticode-signature verification, so the trust anchor is "whoever
  can publish a release to `defessler/DokiDex`." For a single-user private repo this is an accepted,
  bounded risk (GitHub account security + the repo being private). To harden, enable release signing
  (`CODESIGN_PFX_BASE64` in `release.yml`) and pin the signer's certificate thumbprint before the swap.
  Surfaced by a defensive-security ultracode pass.
- **Repo-coupling, stated honestly:** the released exe is the auto-update payload and must live inside
  a cloned repo (the panel shells `doki.ps1`); standalone use elsewhere is unsupported — noted in the
  release workflow. Cut a release with `git tag vX.Y.Z && git push origin vX.Y.Z`.

## 2026-06-15 — Codebase RAG embedder: nomic-embed-text kept (Qwen3-Embedding eval'd → no win)

The `code_search` MCP tool (a sqlite brute-force-cosine RAG over the repo) embeds via a CPU-only
nomic-embed-text-v1.5 server (`:8090`, 0 VRAM). Two retrieval tunings, both **measured** on
`evals/rag-eval.py` (a 14-query recall benchmark — the RAG analogue of the golden-task coder suite, so
retrieval is never tuned blind): a code-preference re-rank (code outranks docs/config/tests — a doc that
*describes* a feature was out-cosining the file that *implements* it) and prepending each chunk's file
path to its embed input (so "auto-updater" matches `Updater.cs`). Combined: **recall@1 6→10, recall@3 9→12**.

- **Embedder bake-off (2026-06-15):** Qwen3-Embedding-0.6B-Q8 (a top general-retrieval model — 1024-dim,
  last-token pooling) scored recall@1 **10** / @3 **12** / @5 **12** — it **ties** the tuned nomic at
  @1/@3 and **loses** at @5, while being ~4× larger (markedly slower CPU embed) and needing
  instruction-prefixed queries. So the lighter 137M nomic stays. Re-run `evals/rag-eval.py` to re-decide
  if a *code-specific* embedder with a clean llama.cpp GGUF (bge-code-v1, a nomic-embed-code, …) appears.
