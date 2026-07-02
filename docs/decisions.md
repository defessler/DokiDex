# Decision log

## 2026-07-01 — `doki code` CLI + the CC-experience plan v2 executed end-to-end (v0.30.0)

The "code side" goal — a coding experience as close to the Claude Code CLI as possible, with the app managing the coder models and teaching its own features — planned by Fable-5 (3 audit forks + 4 verification forks incl. real-CC-docs ground truth and a 2026 landscape check that VALIDATED the bespoke bet), executed as 25 Sonnet-5 leaves, each parent-verified + committed individually (`docs/cc-experience-plan-2026-07.md`). Suite 890 → **1199/1199**.

- **`doki code` (new):** a local terminal coding agent over llama-swap — Read/Grep/Edit/Write/Bash with the **text SEARCH/REPLACE edit protocol** (the Aider/Vibe consensus for 30B locals) + fuzzy applier + reflection; **streaming turns** (llama.cpp `delta.tool_calls` fragment accumulation, verified feasible vs PR #12379/#16932); repo orientation (DOKI/AGENTS/CLAUDE.md + tree + git status, `/init`); context meter + `/compact [instructions]` + auto-compact (~32k healthy set); out-of-repo **sessions** (`--continue`, `/resume`, `/export`); **CC-rule permissions** (persisted allow/deny, `Bash(prefix *)`, deny-first before any prompt); `/plan` mode (schema filtering + the content-edit skip); `@file` + `!shell`; custom commands (`.doki/commands`); opt-in WebSearch/MemoryRecall; `/usage` `/status` `/diff` `/undo`; one-shot scripting (exit codes, piped stdin, `--output-format json`). Bugs fixed en route: Bash stderr-pipe deadlock + un-killable children, `/clear` nuking orientation, JsonElement misreads on resumed sessions.
- **Coder-model management (the app finally manages the CC-style models):** `media-assets/llm-model-catalog.json` (8 entries/12 files, **every SHA-256 human-verified at gate G1** vs live HF LFS pointers, multi-part-aware) → `LlmModelManager` (verify-before-promote installs, UNVERIFIED refused pre-download) → the Models view's **Text models** section + **Tiers** table (configured/on-disk/loaded + web warm-load, honest 202) + **eval badges** (latest-per-task rule over `evals/results.jsonl` — surfacing coder-fast at 5/11, investigate) + sidecar model names on Status. Media installs gained optional sha256 verify; `run-suite.ps1` gained a `-MinPassRate` exit-code gate for the bake-off.
- **Discovery:** `doki help`; an in-app **Help** view rendering the whole docs corpus (whitelisted ids, DOM-only renderer — zero innerHTML; also fixed docs missing from the installer payload); Home **Code** card + six dark-feature cards + truthful model-gated readiness; **Ctrl+K command palette** (26 actions); tier tooltips. Docs refreshed to match everything.
- **Process notes:** planner-seat = the session's live model (smartplan amended); audits run as planner-model forks; one implementation agent on the tree at a time; specs identifier-anchored. **OPEN — G2 (GPU):** the three-way coder bake-off (Qwen3-Coder-30B vs Devstral-Small-2-24B vs Qwen3.6-35B-A3B — both candidates installable from the Models view), streaming feel-check, warm smoke, the coder-fast 5/11 eval investigation. **G3 (design-first):** in-app bake-off pipeline, todo display, subagents, MCP, web-chat code-mode.

## 2026-06-24 — Studio capability auto-discovery + codebase audit/research + security hardening (v0.29.0)

Surfaced the v0.28 model upgrades in the app, then audited + hardened the codebase.

- **Auto-discovery (feat):** the app now DISCOVERS gen kinds + LLM tiers from serving instead of hardcoding them in 3 drifting places. `serving/doki-gen.ps1` `Get-GenKindCatalog` + a `-ListKinds` emitter (mirrors `-BodyOnly`) → C# `CapabilityCatalog` (shell + cache + static fallback) + `GET /api/capabilities`; `LlmTiers` became a data role-table validated against the live llama-swap `/v1/models` list (reusing `StatusProbe`). Result: a **"video + audio" (LTX-2.3) Create pill** + a **"reasoning" (gpt-oss-20b) chat tier** appear automatically. `KindSyncTests` guards C#↔PS drift. (The frontend `loadCapabilities` was completed by hand after a plan-mode reminder leaked into the edit subagents.)
- **User docs:** `docs/tutorial.md` (full DokiGen Studio guide) + `docs/quickstart.md` (5-minute app path), linked from the README — the studio web app had no hands-on guide (the wiki is coding/CLI-centric).
- **Codebase audit (6-agent workflow → `docs/audit-2026-06.md`):** 45 findings; verdict — disciplined codebase (pure/testable services, argv-array shelling = no shell injection, real test suite). The two God-files (`StudioHost.cs` 1,296 lines, `index.html` 2,741 lines) are the dominant structural debt.
- **Security hardening (the 7 audit P1s, +24 tests):** CSRF no-Origin POST hole CLOSED; `edit_image` arbitrary-file-read CLOSED; broken gated kinds (added `Audio`/`Engine`) fixed; `CUDA_VISIBLE_DEVICES` parent-session leak fixed; SHA-256 verification added on model downloads; `SwarmGen.TryHandle` covered. `doki test` green (xUnit 830/830 + Pester).
- **Research → roadmap:** `docs/research-harness-models-2026-06.md` (AI agent-harness architecture + how frontier/open models are built + a per-model comparison + what drives performance & the harness's role; 14 prioritized actions) → fused into `docs/stack-improvement-2026-06.md`. NEXT (eval-gated, not yet applied): per-role tool-call sampling, strip gpt-oss `reasoning_content` from history, GBNF tool-arg grammar + retry, freeze system-prompt bytes for cache reuse, raise `coder-big` prefill batch.

## 2026-06-21 — Full-send upgrade COMPLETE: eval-gate (vision tier promoted) + EchoMimicV3 isolated render (v0.28.0)

Closed out the four-model full-send. All four now live + verified:
- **Eval-gate** (real-image Describe compare + tool-call gate, via subagent): **vision tier PROMOTED 8B -> Qwen3-VL-32B** — it decisively out-reads the 8B on fine UI text/telemetry (read live dashboard numbers + per-service model names the 8B missed entirely), the exact Describe/Verify capability the tier needs. The 8B is kept as `vision-8b` (sub-second warm) for high-volume vision. **gpt-oss-20b** passed the tool-call gate (4/4, zero flakes, ~359 tok/s) + 3/3 direct coding probes -> kept as the on-demand `fast-candidate-gptoss20b`; coder-fast (30B) stays the coding default (gpt-oss's >=91% golden half is unmeasured — needs the candidate registered in the crush harness).
- **EchoMimicV3** moved HELD -> DONE via an **ISOLATED env** (`media/echomimic-iso/`, gitignored; docs/echomimic-isolated.md) — the safe path for its tensorflow 2.15 dep. Own venv (torch 2.11+cu128, TF 2.15, smthemex/ComfyUI_EchoMimic @3a36b00f, minimal ComfyUI on :8198) rendered a talking-head (384², AAC audio, ~14GB peak) on the 5090. The WORKING ComfyUI was confirmed UNTOUCHED (Python 3.12 / torch 2.8 / no TF). It's a separate-launch capability, NOT a `doki gen` kind (the TF env can't live in the main ComfyUI). Finding: the working stack's Wan `.pth` are WanVideoWrapper-format, unreadable by stock ComfyUI loaders -> fetched the Comfy-Org repackaged `.safetensors` (SHA-256 verified).

Resting state verified clean: agent stack up (:8080/:8004/:8005/:8090), media + iso ComfyUI down, GPU idle, no orphans. **Scorecard — all done:** gpt-oss-20b (on-demand) · Qwen3-VL-32B (vision tier) · LTX-2.3 (`doki gen -Ltx`) · EchoMimicV3 (isolated).

## 2026-06-21 — Model upgrades ADOPTED (full-send): gpt-oss-20b + Qwen3-VL-32B (LLM) + LTX-2.3 (video); EchoMimicV3 held

Acting on the four research dives, downloaded + activated + GPU-verified the verified upgrades — parallel fetch via subagents (one per model), sequential GPU bake-off (one 32GB GPU). Targeted the dev repo `D:\Projects\DokiDex` (the active serving root; the saved `InstallRoot=D:\Programs\DokiDex` is a stale v0.8.0 standalone adoption — flagged, untouched).

- **gpt-oss-20b** (`ggml-org/gpt-oss-20b-GGUF`, MXFP4 11.3GB) — a FAST fully-on-GPU LLM tier (no `--n-cpu-moe` offload; ~20GB @131k). Live as llama-swap `fast-candidate-gptoss20b`; smoke-verified (loads + generates).
- **Qwen3-VL-32B** (`Qwen/...-GGUF` Q4_K_M 18.4GB + mmproj 1.1GB) — vision UPGRADE over the 8B; describes images accurately via llama.cpp Qwen3-VL CLIP (PR #16780, in b9616); fits 32GB (~30.7GB). Live as `vision-candidate-32b`; smoke-verified. Both LLMs pending the eval-gate before promotion to default tiers.
- **LTX-2.3** (`Lightricks/LTX-2.3-nvfp4`, 21.7GB, native audio+video) — REQUIRED upgrading the ComfyUI-embedded **torch 2.7.1 -> 2.8.0+cu128** (NVFP4 needs the `float4_e2m1fn_x2` dtype). **Regression-gated:** after the bump, Z-Image + Wan2.2 confirmed still generating (no regression) -> torch stays at 2.8. Rendered a native A/V clip (h264 768x512 + AAC 48k stereo, ~30GB). Wired as **`doki gen -Ltx`** (custom-workflow kind `ltx`, mirrors WanFoley; 276 ps + 126 C# tests green). Deps: the official `Lightricks/ComfyUI-LTXVideo` node + the fp8 Gemma-3-12B text encoder (SHA-256 verified).
- **EchoMimicV3** (talking-avatar) — weights downloaded but **HELD**: its third-party ComfyUI node (`smthemex/ComfyUI_EchoMimic`) needs **tensorflow 2.15** in the working ComfyUI venv (TF+torch fragility = real risk to the production media stack). Deferred to an isolated env per the user's call.

KEY: **torch 2.8 in the ComfyUI env is now load-bearing** for LTX (NVFP4) and verified non-regressing for the incumbents (Wan2.2 / Z-Image / Qwen-Image / nunchaku / Hunyuan-Foley). Targeted dives + parallel subagent fetch + sequential GPU bake-off was the workflow.

## 2026-06-21 — Chat→media round-trip SHIPPED + full capabilities writeup; FOCUS-3 media-frontier research (v0.27.0)

**Round-trip (generate-from-chat, completed).** The chat assistant now generates AND edits images in-thread. Built in parallel via a worktree-isolated multi-agent workflow (3 file-disjoint components), then integrated: (1) `ChatGenCoordinator` — drains `PendingGenStore.Queued()` over the single mutually-exclusive GPU (flip to media → render via the proven `SwarmGen` path → lifecycle rendering+preview → done+ResultRel+sidecar → flip back to the agent LLM); pure NeedsFlip/BuildGenRequest seams + a full GPU-free drain test. (2) `edit_image` tool (refine/img2img — the 5th curated chat tool). (3) inline chat-thread surfacing (queued → preview → final image) + a deliberate **"Render N queued →"** trigger button (auto-rendering is wrong — a drain evicts the chat LLM). 767/767 tests; live HTTP surface verified. The shared seam (PendingGen InitImage/Strength/Preview + FilterQueued) was committed FIRST so the parallel agents forked from a stable API. The trigger button was a seam the parallel split missed — caught by end-to-end verification, not the (green) unit tests. Design: `research-chat-media-roundtrip-2026-06.md`. NOTE: the first real SwarmUI render (live, on the install) is the remaining first-use check — deferred (heavy + real-install side effects); the reused components are proven.

**Capabilities writeup.** `CAPABILITIES.md` — the definitive "everything we support" map, from a 3-agent codebase sweep. Refreshed README (it omitted the whole Studio web app) + superseded/see-also banners on stale design docs (`dokigen-studio-design.md`, `TDD.md`) + a feature-index pointer.

**FOCUS-3 media-frontier research** (`research-focus3-media-frontier-2026-06.md`): lip-sync ANSWERED — EchoMimicV3 (Apache-2.0, 12-16GB) best permissive avatar, MuseTalk best real-time, LatentSync best quality but license-gated; real-time canvas = StreamDiffusion+SD-Turbo (StreamDiffusionV2 is Linux-only video, not the canvas). Video/audio/LLM refresh remain OPEN (candidates surfaced — LTX-2.3-fp8, Wan2.5, ACE-Step-1.5, DiffRhythm2, VibeVoice, Qwen3-Coder-Next, Qwen3-VL-32B — none verified yet).

## 2026-06-21 — Guided Home command center SHIPPED (Phase 1); PowerShell 7 reinstalled

Per "make the app self-guiding": brainstormed (full dialogue) -> spec
(`docs/superpowers/specs/2026-06-21-guided-home-hub-design.md`) -> built + verified Phase 1.

- **Backend (unit-tested):** `HomeCatalog` — the 10 Studio areas as a declarative catalog (Make/Talk/Manage),
  each with blurb + `requires{mode/service/model}` + clickable starters; a PURE readiness resolver (precedence:
  mode-mismatch > missing-service > missing-model > ready) + `SnapshotFrom(StatusDoc)` (GPU group `llm` -> "agent");
  `GET /api/home` joins the catalog with the SAME live status the dashboard uses. 13 unit tests.
- **SPA (live-verified on :5111):** a new DEFAULT `Home` view renders `/api/home` into grouped capability cards
  with live readiness badges (Ready / needs-mode / needs-setup -> routes to where you fix it) + clickable starters
  that launch an area pre-filled, plus a welcome header + GPU/mode meter. Smoke test: `/api/home` returned all 10
  cards with correct readiness (mode `none` -> Create/Chat/etc. needs-mode, Voice needs-setup, Library/Models/Status
  ready); the served SPA carries the Home view.
- **Phase 2 (SHIPPED — v0.26.0):** the cold-load fix (cards render in ~130ms via `GET /api/home/catalog`, readiness
  async), the quick-start box (`GET /api/home/route`: a question -> Chat, else -> Create with an inferred kind — pure
  `RouteQuickStart`), expandable per-card mini-guides (`HomeCapability.Guide`), and a recent-work thumbnail strip
  (reuses `/api/gallery`, hidden when empty). 20 HomeCatalog unit tests; each slice live-smoked on :5111.

**Env fixed:** PowerShell 7 reinstalled (`winget install Microsoft.PowerShell` -> pwsh 7.6.2 on PATH), so the
control-panel build / `doki test` / release run natively again (no more exit `9009`).

## 2026-06-21 — Chat surface advanced: generate-from-chat foundation + long-term memory wired to the EXISTING memory-mcp; guided Home hub in design

Built incrementally atop the v0.7.0 chat surface (TDD, all green via a `pwsh`->`powershell.exe` build shim — see env note).

**P1 generate-from-chat (foundation only).** The `generate_image` tool already queued a gen durably; the missing piece is the render round-trip. Shipped the dependency-zero foundation: the tool now threads the originating **conversation backlink** into the PendingGen (was hardcoded `null`), and PendingGen gained a **render-status lifecycle** (`queued -> rendering -> done/failed` + `ResultRel`, via `SetStatus`). DEFERRED: the renderer + a backend **GPU-flip coordinator** + frontend inline-injection — they add a 2nd controller of the single GPU and need live on-GPU verification (the repo's verify-on-GPU rule).

**P2 long-term chat memory — wire to the EXISTING store, not a greenfield clone.** Correction to the roadmap's "clone KbStore": the repo already ships `serving/memory-mcp/memory_db.py` (sqlite+FTS5), so memory is INTEGRATION. Shipped: a `[Memory]` prompt-injection block in `ChatPrompt.Build` (sibling of `[Documents]`; unconditional injection, NOT a 5th tool — respects the curated-toolset rule); a JSON **CLI** on `memory_db.py`; `MemoryRecall` (shells `uv run python memory_db.py`, degrades to empty) wired into all 3 chat send paths, **gated on `memory.db` existing** so an empty store costs nothing; editable CRUD seams + `/api/memory` GET/POST/DELETE. The SPA memory panel is deferred pending the Home-hub design.

**Guided Home hub (in design).** Per the request to make the app self-guiding, brainstorming a `Home` command-center view: grouped state-aware capability cards (Make / Talk / Manage) with clickable starters + expandable mini-guides, recent-work, a quick-start box, and a GPU/mode meter — catalog-driven. Spec pending user approval.

**Env note:** PowerShell 7 (`pwsh`) is uninstalled on this box — it blocks the control-panel build / `doki test` / release with exit `9009`. Reinstall: `winget install Microsoft.PowerShell`.

## 2026-06-21 — Claude Code stays NATIVE on the local stack (skip cc-switch); GLM-4.7-Flash wired as a gated coder candidate; tool-call gate hardened (#19009)

Three multi-agent (ultracode) evaluations this session; full reports in `docs/eval-cc-switch-2026-06.md` + `docs/eval-glm-4-7-2026-06.md`.

**cc-switch (farion1231/cc-switch) → SKIP; use native Claude Code config.** The 105k-star Tauri GUI swaps provider profiles across ~7 agent tools, but its one relevant capability — an Anthropic↔OpenAI translation proxy — is **redundant here: llama.cpp's server already implements `POST /v1/messages` natively** (verified in b9616) and llama-swap proxies it, so Claude Code targets the local stack with pure config. Adopting it would add a plaintext-key SQLite store, an unsigned auto-updating installer (the SmartScreen/Defender shape we avoid), and whole-file `~/.claude` rewrites for ~zero gain. **Wired instead:** `~/.claude/dokidex.settings.json` (`ANTHROPIC_BASE_URL=http://127.0.0.1:8080`, model `coder-fast`, dummy `ANTHROPIC_AUTH_TOKEN`) + `cc-local` / `cc-cloud` PowerShell functions. Live-verified end-to-end (cc-local → coder-fast → correct output). Note: current Claude Code uses `ANTHROPIC_DEFAULT_HAIKU_MODEL`, not the deprecated `ANTHROPIC_SMALL_FAST_MODEL`.

**GLM-4.7 (z.ai) → only GLM-4.7-Flash is local-viable; wired as an eval-gated bake-off candidate, NOT adopted.** The 355B flagship is cloud-only (4-bit ~192-225GB >> 96GB). GLM-4.7-Flash (30B-A3B) is a credible Qwen3-Coder-30B challenger: 17.5GB UD-Q4_K_XL, full-GPU, **runtime arch `deepseek2`/MLA (`Glm4MoeLiteForCausalLM`) — NOT `glm4moe`**, already supported by b9616 (no bump). Risks pinned: OPEN llama.cpp **#21915** (GLM gibberish on turn 2+ with `q8_0` KV — our exact `coder-fast` flags) and **#19009** (ignores `tool_choice` / loops in required+thinking mode). Wired commented in `serving/llama-swap.yaml` (KV-quant OFF, GLM sampling) + `setup.ps1 -LlmCandidates` (GGUF SHA-256 `b0d4fbc1…`, verified, Unsloth post-Jan-21 build). Expect a close fight vs Qwen3-Coder, not a clear win — gate before any swap.

**Tool-call gate hardened (`serving/test-toolcall.ps1`) for #19009.** A single auto-mode call can't catch the GLM failure mode, so the acceptance script gained **T1b** (forced `tool_choice=required`) + **T3** (multi-hop loop must terminate within `-MaxHops`) + `-GlmSampling`; pure logic factored into helpers with **20 offline unit asserts** (`tests/test-toolcall.test.ps1`, AST-extracted, wired into `doki test`). Zero tool-call flakes stays the bar.

## 2026-06-19 — Legacy `.doc` (OLE binary Word) KB ingest (the LAST v0.15 ingest follow-up) → **DEFER** the reader + **FIX the silent garbage-attach** (clean rejection; NO heavy install)

**Question.** The KB binary-ingest path (`doc_ingest_bin` → `extract_text`) supports `.pdf` (pypdf) and `.docx`
(python-docx), with a gated `-Ocr` scanned-PDF fallback. The last labeled v0.15 ingest follow-up: should we WIRE
**legacy `.doc`** (the Word 97-2003 **OLE2 Compound File** binary format — NOT OOXML; **python-docx does NOT read
it**) → text on a single-user Windows box, or DEFER? And — separately — is a `.doc` upload handled CLEANLY today?

**Today's behavior (verified empirically, NOT assumed).** A `.doc` was **MISHANDLED, not cleanly rejected.**
`extract_text("legacy.doc", oleBytes)` fell through the `.pdf`/`.docx` branch into the `data.decode("utf-8",
"replace")` passthrough. Run against real OLE2 magic (`D0 CF 11 E0 A1 B1 1A E1` + binary body), it returned a
1030-char string that was **~50% U+FFFD replacement chars + control bytes**; `chunk_text` then emitted **1 non-empty
garbage chunk** that would embed + store under the `.doc` source — i.e. a **misleading "attached, N chunks"** with an
unsearchable noise chunk silently polluting the KB. This is exactly the "treated as utf-8 → garbage chunks"
fall-through the follow-up flagged.

**`.doc` → text options (WebFetch-confirmed against primary sources, with honest weight):**
- **Pure-pip pure-Python reader — NONE clean exists.** `docx2txt` is **`.docx`-only** (PyPI: "extract text and
  images from **docx** files"); `mammoth` + `python-docx` are **`.docx`/OOXML-only** too. `olefile` IS pure-pip +
  pure-Python + zero-dep, but it is a **low-level OLE2 CONTAINER parser** only — it lists/reads raw streams
  (`WordDocument`, `0Table`/`1Table`); extracting clean body text requires hand-parsing the FIB + **piece table** +
  reassembling fragments with per-piece encodings (a substantial, fragile binary-format reimplementation), and there
  is **no maintained high-level pure-pip lib** that does this. So the "rides the existing uv-overlay like `.docx`"
  ideal has **no real candidate**.
- **`textract` — does NOT help on Windows.** Even the **actively-maintained `textract` 2.0.0 (2026-04-27)** has **no
  pure-Python `.doc` path**: its `doc_parser.py` shells out to **`["antiword", filename]`**, and its own docs say
  *"antiword is required by the .doc parser (note: **no longer actively maintained**)"* — and **antiword is not
  available on Windows** (apt/brew only; no Chocolatey/winget package). A renewed maintainer doesn't change the
  Windows-`.doc` story at all.
- **LibreOffice headless** (`soffice --headless --convert-to txt`) — robust + cross-format, but a **HEAVY system
  dep** (full office suite) for a legacy niche, and on **Windows specifically** has **documented UTF-8 corruption on
  `.doc`→txt** conversion + is not thread-safe. This is the only genuinely-working local path, but it is the same
  weight class as a gated `-Flag` system binary (cf. `-Ocr`'s UB-Mannheim Tesseract) — too heavy to justify here.
- **antiword** — unmaintained AND not Windows-installable (the textract dep above). Out.
- **`aspose-words` / `spire.doc`** — **commercial/proprietary** ("Free To Use But Restricted, Other/Proprietary
  License"; eval-mode limits). They DO read `.doc`, but a paid/restricted lib is a non-starter for this app. Out.
- **MS Word COM automation** — needs Word installed (not assumable; fragile out-of-proc automation). Out.

**Verdict — DEFER the reader; FIX the rejection.** There is **no clean + light pure-pip path** for legacy `.doc` on
Windows: every option is heavy (LibreOffice), Windows-unavailable/unmaintained (antiword/textract), commercial
(aspose/spire), or a brittle by-hand OLE/piece-table reimplementation (olefile). And **modern Word docs are `.docx`,
which IS already supported.** So per the DECISION RULE, DEFER means **no heavy install / no gated `-Flag` / no recipe
change** — the gated-registry (`tests/gated-registry.ps1`), `setup.ps1`, `doki.ps1`, and the OCR/`.docx`/`.pdf`/`.txt`/
`.md` paths are **all kept byte-for-byte**. The real user-facing win is the **clean rejection** (the niche doesn't
justify a heavy dep; a confusing garbage-attach is the actual bug).

**What changed (the rejection fix ONLY — no install change):**
- `serving/memory-mcp/doc_index.py`: a new catchable **`_Unsupported`** domain error + a **`_REJECTED_EXTS =
  {".doc", ".dot"}`** set. `extract_text` now rejects `.doc`/`.dot` on the **extension alone** (bytes never parsed)
  **BEFORE** the utf-8 passthrough, raising `_Unsupported` with the clear message *"legacy .doc/.dot (Word 97-2003)
  isn't supported — convert it to .docx, .pdf, or .txt and attach that."* It is **DISTINCT from `_ExtractFailed`** (a
  `.doc` is a VALID file, just an unsupported FORMAT — not "corrupt/encrypted"), so the messages stay honest. The CLI
  `doc_ingest_bin` handler maps it to a **DISTINCT exit 7** (alongside 3 = parsers-missing / 4 = corrupt / 5 =
  too-large), so the C# side never shows the misleading embed-down 503 or the wrong corrupt-file text.
- `control/Web/DocSearch.cs`: `MapIngestBinExit` gains an **exit-7** arm surfacing the same clear convert-to message
  (falling back to a clear default when stdout carries no `{"error":…}`) — never "embed server", never "corrupt".
- The `.docx`/`.pdf`/`.txt`/`.md`/`-Ocr` extraction paths + the chunker/embed/retrieval + the `accept=` upload list
  are **untouched** (the picker already omits `.doc`; the server-side rejection covers a dragged/renamed `.doc`).
- TDD: `tests/doc_index.test.py` (**148 → 161**) pins the `extract_text` `.doc`/`.dot` rejection (incl. case-
  insensitive `.DOC`/`.DOT`, the empty-bytes/extension-only path, and `_Unsupported` ≠ `_ExtractFailed`) + the CLI
  exit-7 mapping; `DocSearchTests` (**607 → 608**) pins the `MapIngestBinExit` exit-7 message contract.

**When to revisit.** If a robust local `.doc`→text path is later wanted, the only credible route is a **gated
`-LibreOffice` (or `-DocConvert`) sidecar** — a winget/Program-Files `soffice.exe` running `--headless --convert-to
txt` as a pre-extraction step, registered in `tests/gated-registry.ps1` with a `SystemFile` shape **exactly like
`-Ocr`'s Tesseract** (mind the Windows UTF-8-on-`.doc` corruption — convert to a UTF-8-safe filter or `.docx` first).
Or, if a **maintained high-level pure-pip legacy-`.doc` reader** ever appears (one that reassembles the FIB/piece
table, not just olefile's raw streams), wire it lazily into `extract_text` on the uv-overlay mirroring the `.docx`
path. Until one of those exists, **`.doc` stays cleanly rejected with the convert-to-.docx message** and `.docx` is
the supported Word format.

**Sources.** docx2txt (`.docx`-only) — pypi.org/project/docx2txt ; textract `.doc`→antiword + "antiword no longer
maintained / Windows-unavailable" — pypi.org/project/textract, the repo `textract/parsers/doc_parser.py`
(`["antiword", filename]`) + readthedocs installation ; olefile (pure-Python OLE2 **container** only) —
github.com/decalage2/olefile + the WordDocument/piece-table structure (learn.microsoft.com) ; aspose-words
(commercial "Free To Use But Restricted, Other/Proprietary") — pypi.org/project/aspose-words ; LibreOffice headless
`--convert-to txt` + the Windows UTF-8-corruption/not-thread-safe caveats — ask.libreoffice.org +
github.com/scivision/office-headless.

## 2026-06-19 — Best-local-video census (HunyuanVideo / Mochi-1 / CogVideoX / HunyuanVideo 1.5 / Kandinsky 5 Lite) → **DEFER** (Wan 2.2 + LTX cover the 32GB spectrum; NO install/recipe change)

**Question.** Among the notable open text/image-to-video models, is there one that is a clearly **BETTER quality
default** than DokiDex's **Wan 2.2 TI2V-5B** (the `doki gen -Video` default), OR a genuinely **COMPLEMENTARY tier**
that the existing Wan+LTX lineup doesn't cover (a specific strength — longer clips, better motion, a niche) — for a
**32GB single-user box**? This is the same shape as the standalone-TTS census above: it asks ONLY about the *video
default + tiers*, not the lip-sync / V2A / talking-video sidecars. The existing lineup is already **strong**:
**Wan 2.2 TI2V-5B fp16** (the `-Video` default, live-validated 832×480×49 in 53s @ 13.8GB) + the gated **Wan 2.2 T2V
A14B GGUF dual-expert** Q4_K_M (`-Quality`, StepSwap) + **LTX-Video ltxv-2b-distilled** (`-Video -Fast`, the
near-real-time fast-draft tier) + **WanFoley** (video→audio) + **InfiniteTalk/LatentSync** (lip-sync). The 2026-06
research already picked Wan 2.2 as SwarmUI's recommended best-local video; **LTX-2/2.3 (22B) was separately
evaluated and parked** as too-heavy / nascent-loader (`docs/frontier-roadmap.md` Tier-2 notes: official floor is
32GB+ VRAM = exactly the 5090, and SwarmUI's LTX2 path previously had no loadable checkpoint).

**Census (cited).**
- **HunyuanVideo v1 (13B)** (`tencent/HunyuanVideo`; Diffusers mirror `hunyuanvideo-community/HunyuanVideo`) — **DOMINATED on 32GB.** The
  full 13B DiT (~25.6GB bf16 transformer + the ~12GB MLLM text encoder + CLIP + VAE) blows **over 32GB** before
  activations; even GGUF/fp8 repacks of the 13B leave too little headroom for the long-frame attention at a useful
  resolution, and the quality it buys over the 5B doesn't open a tier Wan 5B + A14B don't already span. License is
  Tencent Hunyuan Community (the same EU/UK/SK restriction DokiDex already accepts for Foley).
- **Mochi-1 (10B)** (`genmo/mochi-1-preview`) — **DOMINATED (non-native).** Genmo's AsymmDiT is **480p-only**
  (preview) and is **not a SwarmUI-native class** — it needs a ComfyUI custom-node path (node-authoring, the same
  wall InstantID/InfiniteTalk hit), so it can't ride the existing `-Video` recipe. 480p preview-grade output is a
  lateral-to-worse step from the 5B's validated 832×480, for a node-authoring cost.
- **CogVideoX (5B)** (`THUDM/CogVideoX-5b`) — **DOMINATED (non-native).** Same arch class as the 5B size-wise but
  **not SwarmUI-native** (CogVideoX-Fun / the THUDM nodes are a ComfyUI custom-node path), and its quality/motion is
  not a measured win over Wan 2.2 5B. Adds a node-authoring cost for no default-beating or tier-opening gain.
- **HunyuanVideo 1.5 (8.3B)** (the smaller native-tractable refresh) — **fits but LATERAL.** This is the only
  candidate that is **plausibly SwarmUI-native AND 32GB-feasible** via a community **GGUF Q4_K_M (~5.09GB**, the
  `jayn7` HF repo) into `Models/diffusion_models` (CFG ~1 distilled / ~6 base — standard ComfyUI HunyuanVideo-1.5
  practice, NOT a model-card spec; confirm at bake-off). But it is a
  **lateral bake-off alternative** to Wan 2.2 5B — same tier, no measured quality win, no distinct axis (clip
  length / motion / resolution) that Wan 5B + A14B don't already cover. A bake-off candidate, not a default-beater
  or a new tier.
- **Kandinsky 5 Lite (2B, 10s)** (the genuinely-distinct axis) — **DISTINCT but BUGGY/UNPROVEN.** Its one real
  differentiator is **~10s native clips** (vs the 5B's ~2s/49-frame and LTXV's short drafts) — the only axis in this
  whole census that Wan+LTX don't cover. But its **SwarmUI path is buggy/unproven** (the loader/class support is not
  a clean native ride; it needs node-authoring to wire reliably), so the longer-clip win is not bankable at rest.
  An unproven, buggy path is exactly the kind of blind-authoring this log refuses to ship (the InstantID/InfiniteTalk
  posture: don't wire a model whose native path can't be verified).

**Verdict — DECISION RULE branch taken: DEFER (the 2nd branch).** **None beats Wan 2.2 as the default**, and **none
opens a gated tier the existing Wan 5B + A14B + LTXV miss** with a *verified* 32GB checkpoint + recipe. The two
remaining live candidates each fail the bar in a different way: **HunyuanVideo 1.5 8.3B** native-fits but is a
**lateral** same-tier bake-off alt (no measured win, no distinct axis) — not a clearly-better default and not a
complementary tier; **Kandinsky 5 Lite 2B** is the **only distinct axis** (10s clips) but its SwarmUI path is
**buggy and unproven** (needs node-authoring), so it is not a *verified* checkpoint+recipe. HunyuanVideo v1 13B
(over 32GB), Mochi-1 (480p non-native), and CogVideoX (non-native) are dominated. Per the DECISION RULE, DEFER means
**add ONLY this note and make NO install/recipe change** — so **every existing video + image recipe is kept
byte-for-byte**: the Wan 2.2 TI2V-5B `-Video` default (`wan2.2_ti2v_5B_fp16.safetensors`), the gated A14B GGUF
dual-expert `-Quality` arm, the LTXV `-Video -Fast` draft tier, WanFoley, and InfiniteTalk/LatentSync are all
untouched. No `setup.ps1` switch, no `model-catalog.json` row, no `doki-gen.ps1` recipe arm, and no GenCli/GenArgs
forward was added (no forwarded switch was touched, so no GenCliTests parity work was needed).

**When to revisit.** (1) **Kandinsky 5 Lite** is the one to watch — if SwarmUI gains a **clean native loader** for
its class (so the 10s-clip axis becomes a *verified* checkpoint+recipe, not a buggy node-author), revisit it as a
gated **longer-clip** tier (the genuinely-complementary axis Wan+LTX miss), wired ADDITIVELY exactly like the A14B
`-Quality` add (a `-Models full` checkpoint or a `-Flag` + a catalog row + a template-sourced recipe), with Wan
TI2V-5B staying the byte-for-byte default. (2) **HunyuanVideo 1.5 8.3B** is worth a future **bake-off** only — pull
the GGUF Q4_K_M (~5.09GB, `jayn7` repo) into `Models/diffusion_models` and A/B it head-to-head against the 5B at
matched res/frames (CFG 1 distilled / 6 base); adopt only on a *measured* quality win, and record the A/B either way
(a clean negative is as useful as a win, same discipline as the LTX-2.3 recon backlog). (3) LTX-2.3 (22B) remains
parked per `docs/frontier-roadmap.md` (32GB+ floor; re-recon SwarmUI's LTX2 loader against an `unsloth/LTX-2.3-GGUF`
checkpoint). Until one of these flips to a *verified, default-beating-or-tier-opening* result, **Wan 2.2 + LTX cover
the 32GB video spectrum** and the lineup stays as-is.

**Sources.** HunyuanVideo 13B — huggingface.co/tencent/HunyuanVideo ; Mochi-1 480p preview —
huggingface.co/genmo/mochi-1-preview ; CogVideoX-5b — huggingface.co/THUDM/CogVideoX-5b ; HunyuanVideo 1.5 GGUF —
the `jayn7` HF repo (Q4_K_M ~5.09GB → `Models/diffusion_models`) ; Kandinsky 5 — ai-forever/Kandinsky-5 (10s Lite
variant). Wan 2.2 5B live-validation + the A14B-doesn't-fit-32GB finding + LTX-2/2.3 parking are in this log and
`docs/frontier-roadmap.md`.

## 2026-06-19 — Coexist-with-chat standalone TTS census → **DEFER on the default** (Chatterbox stays) + wire the **ONE** gated `-Kokoro` alternative

**Question.** Is a *different* standalone TTS a better **default** for the coexist-with-chat speech path — the
:8004 Chatterbox server in the **llm group** that comes up WITH the coder (the chat `/api/speak` → :8004 path,
voice readback in agent mode with no media GPU-switch)? Requirements: (1) coexist with the chat LLM (small VRAM,
OpenAI-compatible server in the agent profile), (2) custom assistant voice = **zero-shot cloning**, (3)
uncensored, (4) clean license, (5) a real server you don't have to build. (This is **not** the already-shipped
`-TtsSuite` 15-engine ComfyUI node — that's the gated media-group extra-engines path.)

**Census (cited).** Chatterbox (Resemble AI) via devnen/Chatterbox-TTS-Server — MIT, ~4GB, zero-shot cloning,
uncensored (Perth watermark stripped at install), mature OpenAI `/v1/audio/speech` + cloning + Web UI, knobs
already wired into `control/Web/Tts.cs`. Kokoro-82M (hexgrad, Apache-2.0) via remsky/Kokoro-FastAPI — tiny
(<2GB / CPU-capable / RTF ~0.03), mature OpenAI server, but **NO cloning** (54 fixed preset voices).
Fish-Speech/OpenAudio-S1 — top-tier quality + cloning, but **Research/Non-commercial weight license** (a real
downgrade from MIT) + heavier SGLang server. XTTSv2 — CPML non-commercial + Coqui defunct (hard no). Orpheus —
Apache but 3B (worst coexistence), cloning/streaming bugs open upstream, community-only OpenAI server. Piper —
tiny/MIT but no cloning. Parler — no zero-shot cloning, weak server story.

**Verdict — DECISION RULE branch taken: DEFER on the default, but wire the ONE worthwhile gated alternative.**
Chatterbox is the **only** option that wins all five requirements at once, so it stays the **byte-for-byte
default** (`Tts.cs` Base=:8004, the agent-profile `tts` entry in both `doki.ps1` and `ServiceRegistry.cs`,
group=llm/vramGB=4, the loopback-bind + watermark-strip, the chat `/api/speak` path — all untouched). The one
genuinely worthwhile move is a **gated, additive Kokoro** alternative on the axis that actually differs for a
single-user box — **footprint**: <2GB / CPU-capable / near-zero GPU contention for snappy generic narration when
the GPU is busy with the coder. It loses cloning entirely, so it can **never** be the custom-voice default —
strictly a toggle.

**What shipped (additive only — mirrors the `-TtsSuite` gated posture):**
- `setup.ps1 -Kokoro`: clones remsky/Kokoro-FastAPI into `kokoro\`, own cu128 venv (`.deps-ok` sentinel,
  resumable), loopback-bound on the **new :8006** (not :8004 Chatterbox / :8005 STT). Never touches the devnen
  Chatterbox clone or its :8004 bind. Install-time it also **pre-fetches the Kokoro-82M weights** via the repo's
  own `docker/scripts/download_model.py --output api/src/models/v1_0` (pulls `kokoro-v1_0.pth` + `config.json`),
  guarded idempotent (skip if the `.pth` exists) + Warn-on-failure — so first `up` just starts an already-
  provisioned server (it does NOT lazy-download on first request, unlike Chatterbox's voice model). The launcher's
  `MODEL_DIR=src\models` resolves to `<repo>\api\src\models` and the loader appends the `v1_0\` prefix from
  `pytorch_kokoro_v1_file = "v1_0/kokoro-v1_0.pth"`, so it reads exactly the download's `--output` dir.
- `serving/start-kokoro.ps1`: clones start-tts.ps1's shape (`-Detach`/`-PidFile`/`-LogFile`), uvicorn on
  127.0.0.1:8006.
- A new `kokoro` service in **both** `doki.ps1` `$Services` and `ServiceRegistry.cs` (group=llm, vramGB=2,
  health `/health`, ui `/web`, `requires` the kokoro venv python). **NOT** added to any default profile — it's
  skipped cleanly until `-Kokoro` installs it (exactly how `-TtsSuite` stays gated).
- `Tts.cs`: an optional `Engine` field on `SpeakRequest` (`ResolveBase`/`ModelFor` pure routers) — `"kokoro"` →
  :8006 + drops the expressive knobs; null/blank/`"chatterbox"`/unknown → the :8004 Chatterbox default. The chat
  `/api/speak` path passes no engine, so it stays Chatterbox byte-for-byte. Graceful degradation preserved (the
  error message now names the resolved port).

**Sources.** Chatterbox MIT — github.com/resemble-ai/chatterbox ; server — github.com/devnen/Chatterbox-TTS-Server.
Kokoro-82M Apache-2.0 — huggingface.co/hexgrad/Kokoro-82M ; server — github.com/remsky/Kokoro-FastAPI
(in-tree `pyproject` `version = "0.6.0-rc1"` on the cloned default branch; latest published git tag/release is
v0.5.0). **Pin the `[gpu-cu128]` extra, NOT `[gpu]`** — confirmed against that `pyproject.toml`: `[gpu]` pins
`torch==2.8.0+cu126` (Blackwell-wrong) while `[gpu-cu128]` pins `torch==2.8.0+cu128`, which the cu128 wheel
installed first satisfies; the repo declares its torch index only in `[tool.uv.sources]` (plain pip ignores it),
so setup passes the `download.pytorch.org/whl/cu128` index explicitly. (Recorded so the cu126 mistake isn't re-
introduced.) The weight pre-fetch path `docker/scripts/download_model.py --output api/src/models/v1_0` is the
repo's documented model-download step (README + the script's argparse: `--output` required, no default).
Fish weights Research/Non-commercial — github.com/fishaudio/fish-speech.

**On-GPU / LABELED confirms (render-unverified at rest — no GPU in CI):** the repo URL + the `[gpu-cu128]` extra +
the cu128 index + the `download_model.py`→`api/src/models/v1_0` step are upstream-sourced (above), but the live
stack is a first-run-on-GPU confirm: (1) the cu128 torch + `.[gpu-cu128]` editable-extras resolve under plain pip,
(2) `download_model.py` lands `kokoro-v1_0.pth` where the launcher's `MODEL_DIR` reads, (3) espeak-ng
phonemization loads via `PHONEMIZER_ESPEAK_LIBRARY`, and (4) `/v1/audio/speech` actually synthesizes on the GPU.

**When to revisit.** If a future model wins ALL five requirements AND beats Chatterbox on quality with a clean
permissive license + a drop-in OpenAI server, reconsider the default. Fish/OpenAudio is the only candidate worth
a future gated *max-quality* alt — hold until its weight license is no longer Research/Non-commercial. Tests:
`tests/setup-helpers.test.ps1` AST-pins the `-Kokoro` block; `ControlPlaneTests` pins the `kokoro` service
sync; `TtsTests` pins the engine routing (Chatterbox stays default).

## 2026-06-19 — TRUE real-time (StreamDiffusion/LCM) follow-up to the shipped live canvas → **DEFER** (evaluated, not built)

The v0.11 **F2** ask was a *true* multi-fps (~30 fps) real-time canvas, the StreamDiffusion/LCM
follow-up to the **shipped** real-time canvas — which is an **honest per-POST Z-Image-Turbo img2img
loop at ~1-3 renders/sec** (each debounced stroke-batch rides the existing `GenerationJobs` single-flight
`_gpu` gate as an **Ephemeral** `GenJob`: writes to `%TEMP%`, skips the Library sidecar via
`GenJob.ShouldPersist`, hidden from `Recent()` via `GenJob.FilterRecent`; the underlying gen is
`SwarmGen.RunAsync`'s one-WS-per-render `GenerateText2ImageWS` path). **Decision-rule branch taken:
DEFER** — record the honest evaluation, make **NO** install/recipe/C# change, keep the shipped per-POST
canvas byte-for-byte the default. Three grounded reasons it is **not** worth it for a single-user 32GB box:

- **The fps win is gated on a quality drop already rejected.** The published StreamDiffusion 30–91 fps
  numbers (91 fps SD-Turbo on a 4090; 100+ fps with large batches) are **SD-Turbo- / SD1.5-class** (SD-Turbo
  is distilled **SD2.1**, `stabilityai/sd-turbo`; the genuinely-SD1.5 path is the LCM alt, dreamshaper-7 +
  lcm-lora-sdv1-5) and the headline fps **requires TensorRT engine compilation** (the upstream
  `StreamDiffusionTensorRTEngineLoader` node; note the paper's ~59.6× at 1 step is attributed to *all* its
  proposed strategies combined with mature acceleration tooling, **not** TensorRT in isolation — but without
  engine compilation the real-time fps is not reached). Either path is a meaningful step down from **Z-Image**,
  which this very log (the `-Nunchaku` / Z-Image entries above) confirms as DokiDex's **#1 photoreal default +
  real-time-canvas base**. So true real-time means shipping a *second, worse-looking* model **and** maintaining
  a per-resolution/per-GPU TensorRT build step (brittle, on-GPU). Sources (fps/quality claims are upstream,
  not independently benchmarked here): https://arxiv.org/pdf/2312.12491 ; https://huggingface.co/stabilityai/sd-turbo

- **True real-time is a NEW ARCHITECTURE, not a workflow.** The maintained real-time line
  (`livepeer/comfystream` + `livepeer/ComfyUI-Stream-Pack` + `pschroedl/ComfyUI-StreamDiffusion`, the
  StreamDiffusion V2 / Daydream lineage; upstream `cumulo-autumn/StreamDiffusion`) is a **persistent
  WebRTC server** that keeps the diffusion pipeline **RESIDENT in VRAM** and streams frames
  bidirectionally. That is the *opposite* of DokiDex's per-POST model (open WS → send one `-BodyOnly`
  body → drain `gen_progress`/`preview` → first image/error frame is terminal → close → download
  artifact, all in `SwarmGen.cs`). You **cannot** reach 30 fps by POSTing faster: the entire speedup
  comes from **not** reloading/re-encoding per frame, which the runner does *by construction* (fresh
  `GetNewSession` + full per-prompt graph every render = structurally per-frame-cold). Reaching it needs
  a **large new subsystem**: a long-lived resident-pipeline service + a persistent WS/WebRTC streaming
  endpoint + a new GPU-ownership model (vs the single-flight `_gpu` gate) + new lifecycle/teardown —
  bypassing `GetNewSession`/`GenerateText2ImageWS` entirely. A per-POST StreamDiffusion-*checkpoint*
  render WOULD run through the existing `comfyuicustomworkflow` runner, but that delivers only a
  **faster still**, NOT the resident 30 fps stream F2 wants.

- **The ~1–3 fps you have is already "good enough" interactive AND strictly better-looking.** For a
  single user sketching, ~1–3 renders/sec of **Z-Image** quality beats 30 fps of SD-Turbo quality for
  almost every real use, and the 32GB box can't trivially hold a resident StreamDiffusion engine +
  TensorRT build + the rest of the media stack hot.

**Node maturity (the weak point, recorded honestly so a future revisit doesn't re-discover it):**
`jesenzhang/ComfyUI_StreamDiffusion` is **dead/dormant** (155★, 13 commits, "input latent not
implemented", img2img needs `batch_size=1`) — do not build on it. `ryanontheinside/ComfyUI_RealtimeNodes`
is **utility-only** (active 81★, but explicitly **no** StreamDiffusion/LCM pipeline — value/motion/MediaPipe
control + FPS-overlay nodes). The real path — `pschroedl/ComfyUI-StreamDiffusion` (~6★, niche) **with**
`livepeer/comfystream` (a WebRTC **server**, not a plain node) + `ComfyUI-Stream-Pack` — is exactly the
heavy resident-pipeline subsystem above, not a Foley/InstantID-style node clone.

**Verified shopping list, kept ON ICE (no `setup.ps1`/`doki-gen.ps1` change made):** a light 32GB-feasible
model is `sd_turbo.safetensors` (**5.21 GB verified single-file**,
https://huggingface.co/stabilityai/sd-turbo/resolve/main/sd_turbo.safetensors → `models/checkpoints`); the
LCM alternative is an SD1.5 base (e.g. `Lykon/dreamshaper-7`) + `latent-consistency/lcm-lora-sdv1-5`
(~135 MB → `models/loras`). **Either way you ALSO need on-GPU TensorRT engine compilation** for the
headline fps (per `StreamDiffusionTensorRTEngineLoader`) — flagged on-GPU/brittle the same way the
InfiniteTalk block flags its unconfirmed 32GB fit. Even a clean gated `-RealtimeFast` **install** (node +
`sd_turbo` + a `comfyuicustomworkflow=RealtimeFast` alias, exactly the `-FaceId`/`-InfiniteTalk` posture)
would only ship the **faster-still** kind — it does **not** deliver real-time — and would still introduce a
second, lower-quality model whose only F2 value is unlockable by the deferred resident-pipeline build. So
shipping it now buys a quality regression for no real-time payoff; that is why the install is **deferred too**,
not just the wiring.

**What would flip this to WIRE:** a future **Z-Image-Turbo** (or **Nunchaku NVFP4 Z-Image**) distilled UNet
that is **StreamDiffusion/LCM-compatible** — the quality objection evaporates and the resident-pipeline
build (resident service + persistent WS/WebRTC transport + new GPU-ownership model + the repointing of the
browser live-canvas loop) becomes worth scheduling. Until then, the cheap intermediate win is the
**existing** path pushed harder (lower steps/res on the ephemeral render, keep SwarmUI warm) — same
architecture, no new model, no quality regression. The shipped per-POST Z-Image live canvas + its
live-loop JS stay the untouched default.

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
the first curated in-process tool `search_library`; `ParseToolCalls` synthesizes unique ids,
the echoed assistant turn carries `content:null`, the per-hop transcript shaping is pure +
tested); **Pn.2** added the next two curated tools — `web_search` (DuckDuckGo via the
`uvx ddgs` sidecar) and `code_search` (semantic RAG over this repo via `code_index.py`'s
`search` dispatch over the :8090 embed server), each degrading gracefully (never throw, never
hang — the child is killed on the per-tool timeout) when its sidecar/index/server is absent.
So the registry now ships **THREE** tools, and the small-curated-set discipline (open models
lose tool-selection accuracy as the list grows — keep it small + sharp) still holds at three.
Only **generate-from-chat** (and any GPU handoff) stays deferred as a future gated tool.
Suite **376 → 452** green throughout; Debug+Release clean each commit.

**Model-adds — shipped the one cleanly-wireable add:** the **anime SDXL pack**
(Illustrious-XL v1.0 + Animagine XL 4.0) as gated `-Models full` downloads + matching
`model-catalog.json` rows so they surface in the Studio picker (and via SwarmUI's native
picker + the `-Model` override; `ModelManager` resolves by filename). URLs HF-tree +
live-HEAD verified; an AST-driven `setup-helpers` test pins the entries. Self-contained
SDXL checkpoints route through the existing image recipe — no recipe/node work needed.
On-GPU load + image quality is the labeled remaining step (first full checkpoints in the
kit vs the existing DiT/unet models).

**Remaining model-adds = gated follow-ups (NOT blind-shipped, per verify-before-ship):**
PuLID-Flux + InfiniteTalk (ComfyUI custom-node / sidecar integrations), TTS-Audio-Suite
(a ComfyUI-node vs the standalone Chatterbox `:8004` architecture decision), and Nunchaku
NVFP4. Each needs multi-component/node integration, a recipe tuning pass, or an architecture
decision that requires on-GPU verification — none a clean unit-testable slice. Full map in
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

**Update — the Wan 2.2 A14B GGUF quality-VIDEO tier SHIPPED as a gated integration** (moved off
the gated-follow-ups list above), mirroring the music `-Quality` discipline. An opt-in **`-Quality`**
on `doki gen -Video` swaps the **Wan 2.2 5B** default → the **Wan 2.2 T2V A14B GGUF dual-expert**
pair (Q4_K_M, ~9.65GB each — the size cut vs the fp8 ~13.3GB experts that OOM'd 2026-06-14). The
dual-expert wiring is **AUTHORITATIVELY doc-sourced** from SwarmUI's `docs/Video Model Support.md`:
SwarmUI has no auto-pairing — it reuses its image-refiner **StepSwap** as a noise-level step-swap,
so **base = HIGH-noise expert**, **Refiner Model = LOW-noise expert**, `refinermethod=StepSwap`,
`refinercontrolpercentage=0.5`; `cfg=5` is the doc's T2V-14B reference, `sigmashift=8` the doc
default carried from the 5B. Wired on **both** PS (`Get-GenRecipe` video arm `elseif ($Quality)`)
and C# (one gate widened: `GenArgs.cs` `r.Kind is "music" or "video"`) with **parity tests** (a
`doki.ps1 -Video -Quality -BodyOnly` seam test catches a dropped forward). Gated `-Models full`
downloads (QuantStack `Wan2.2-T2V-A14B-GGUF`, HighNoise/ + LowNoise/ subfolders) + `model-catalog.json`
rows; TE (umt5_xxl) + VAE (wan2.2_vae) already on disk (NOT re-fetched). The **5B default + `-Fast`
LTXV stay byte-for-byte unchanged** (the arm is `elseif ($Quality)` only). Suite **454 → 455 (+1 C# test)**
green; PS recipe 155 / setup-helpers 46 green.
**On-GPU / GATED remaining confirms (NOT verified at rest — no GPU in CI):** (1) the **`refinermodel`**
body key is DERIVED via `CleanTypeName` from the source display name "Refiner Model" — confirm against
a live `/API/ListT2IParameters`; (2) **steps + sampler/scheduler** for the non-distilled 14B are NOT
doc-sourced (uni_pc/simple + 20 steps carried from the 5B as the start point, tune live); (3) the live
**32GB fit** of the dual ~9.65GB Q4_K_M experts held across StepSwap; (4) the **city96 ComfyUI-GGUF
node** install / GGUF arch auto-detect. Decision-rule branch taken: dual-expert wiring IS doc-sourced
→ wire it fully (gated downloads + catalog + `-Quality` arm on both sides, TDD), with the three derived/
unsourced items labeled as the on-GPU step.

**Update — the Qwen-Image base GGUF (in-image TEXT) tier SHIPPED as a gated integration** (moved off
the gated-follow-ups list above), mirroring the FLUX.2 Klein / Wan A14B model-add discipline. The
**Qwen-Image base GGUF** (`Qwen_Image-Q4_K_M.gguf`, ~13.1GB — QuantStack's Q4_K_M of the NON-distilled
t2i unet, the strong in-image-text model) is added as a gated **`-Models full`** download + a
`model-catalog.json` row (so it surfaces in the Studio picker / SwarmUI's native picker / via `-Model`)
+ an **additive, image-only `Get-ModelFamilyOverride`** that applies **steps 20 / cfg 4 / euler / simple**
when a `Qwen_Image-*.gguf` checkpoint is selected. The override is doc/template-sourced: `steps≈20` +
`cfg=4` are SwarmUI's `Model Support.md` quality/speed band ("CFG=4 ... at a performance cost", "normal
~20 works"), and `euler`/`simple` are the official `image_qwen_image.json` KSampler. It REUSES the
**Qwen2.5-VL TE + Qwen-Image VAE already installed by the Qwen-Image-Edit-2511 lines** (`$te`/`$vae` — no
re-download; only the unet is new, NOT the GGUF repo's redundant 254MB VAE). Opt-in only: selecting any
other model leaves every existing path byte-for-byte unchanged (the override returns `@{}`), and no
recipe default / download URL / catalog row / test assertion changed.
**On-GPU / LABELED confirms (render-unverified at rest — no GPU in CI):** (1) the **GGUF arch
auto-detect** + the one-time **city96 ComfyUI-GGUF node** install popup that SwarmUI raises on first GGUF
load (headless-accepted via the existing `InstallConfirmWS` path); (2) the exact **base step/cfg** within
the doc-supported **20–50 steps / cfg 4** band (the 20/4 start point, tune live); (3) the live **32GB fit**
+ render quality. The override knobs are additive + unit-tested at rest; output quality is the on-GPU step.

**Update — InstantID face-identity SHIPPED as INSTALL-WIRING ONLY (workflow JSON deferred to on-GPU authoring).**
Picked **InstantID** (`cubiq/ComfyUI_InstantID`) over **PuLID-Flux** for the gated face-ID integration, decisively
on three grounded reasons: **(1) base on disk** — InstantID is **SDXL** and reuses the anime
**Illustrious/Animagine SDXL** checkpoints already shipped (`setup.ps1:549-550`), so **no new base download**;
PuLID-Flux *requires* **FLUX.1-dev (~12-22GB)** which DokiDex does NOT ship. **(2) node maturity** — cubiq is
maintenance-mode/stable; both PuLID-Flux nodes are weaker (balazik = Alpha V0.1.0 prototype; the sipie800 fork is
**formally discontinued** 2025-10-07). **(3) weight size** — InstantID add-on is **~4.55GB** (1.69GB IP-Adapter +
2.5GB ControlNet + 361MB antelopev2) vs PuLID's weights **plus** the multi-GB FLUX base. PuLID-Flux is **deferred**
until/unless a FLUX base tier is added.

**Shipped (the gated install only):** a **`-FaceId`** sidecar switch on `setup.ps1` (mirrors `-Sam`/`-Demucs`/`-Train`)
that clones the cubiq node into `custom_nodes\ComfyUI_InstantID`, pip-installs `insightface onnxruntime-gpu` (the
**-gpu wheel ONLY**, not plain `onnxruntime` too: with both present the CPU build can win the `onnxruntime` module
namespace so CUDAExecutionProvider silently fails to register and antelopev2 falls back to slow CPU — a known InsightFace gotcha)
via the same 3-candidate comfy-python probe the Foley node uses, and `Get-Model`s the 3 InstantX weights
(`$ix = InstantX/InstantID resolve/main`) into ComfyUI's `models\{instantid,controlnet,insightface}` dirs
(antelopev2 zip unzipped, with a comment that the node can auto-download it on first run if the community mirror
fails). Ergonomic alias: `doki gen -FaceId -InitImage <face.png>` -> `Resolve-GenKind`->`faceid`->
`comfyuicustomworkflow=InstantID` (the reference face rides the **init-image** channel, NOT SwarmUI's own
IP-Adapter-revision flag — a different path). All sizes/URLs HF-tree-verified; an **AST-driven `setup-helpers`**
block pins the node clone + the 3 `$ix` weight entries + a guard that the SDXL path pulls **no FLUX/PuLID** weights;
`doki-gen` pins the new kind/recipe + the IP-Adapter-flag negative.

**Decision-rule branch taken: install URLs verified, but NO authoritative SwarmUI-hook-ready workflow JSON is
sourceable -> wire ONLY the gated install + this note; do NOT commit a blind workflow JSON.** The upstream example
(`examples/InstantID_basic.json`) is a ComfyUI **UI-graph** export (not SwarmUI's **API-prompt** `CustomWorkflows`
format), and hardcodes both the checkpoint (`AlbedoBaseXL`) and a reference jpg — converting it to API-prompt,
re-pointing it to Illustrious/Animagine, and injecting the SwarmUI `${prompt}` / reference-image placeholders is
**blind-authoring**. So the runnable `media-assets\InstantID.json` is the **on-GPU authoring step**: author/convert
+ validate a gen against the live node, THEN commit it (same posture WanFoley implies). Until then the `-FaceId`
block installs node+weights and the workflow copy is **guarded by a `Test-Path` that Warns** if `media-assets\InstantID.json`
is absent (identical to the WanFoley copy). **On-GPU LABELED confirms:** (1) node load + face-ID **render quality**;
(2) the antelopev2 mirror's **5 `.onnx`** contents — a MIX, not all detectors (glintr100 = recognition, genderage =
attribute, scrfd/det = detection) — (community mirror, not byte-verified — or let the node
auto-download); (3) the ControlNet target **subfolder** the node's loader expects. **Every existing path is
byte-for-byte unchanged** (the `faceid` kind/recipe is purely additive; no default/URL/catalog row changed).

**Update — PuLID-Flux face-identity SHIPPED as INSTALL-WIRING ONLY (the base InstantID deferred is now un-blocked; workflow JSON + node-load deferred to on-GPU).**
The InstantID entry above **deferred PuLID-Flux on two premises** — (1) "DokiDex lacks a FLUX.1-dev base (~22GB)" and
(2) base gating — and **this research partially overturns both**, so PuLID-Flux now ships as a GATED `-Pulid` sidecar
mirroring `-FaceId`. **(1) Base un-blocked via a NON-GATED fp8 path.** The canonical `black-forest-labs/FLUX.1-dev`
**is hard-gated** (contact-info license click-through, not scriptable); the convenient all-in-one `Comfy-Org/flux1-dev`
is **license-restricted** (FLUX.1 [dev] Non-Commercial) **but NOT hard-gated** — no contact-info banner, per-file
resolve links render, so it is technically scriptable. We use Kijai's fp8 instead for a **footprint** reason, not a
gating one: the full-precision all-in-one is a poor 32GB fit, whereas the **~17 GB fp8** base slots into 32GB with
room to spare — fp8 is the practical 32GB path. **`Kijai/flux-fp8` is verified UNGATED** (`gated:false`; resolve 302s
to a public CDN, not a login wall): `flux1-dev-fp8.safetensors` (11.9 GB fp8_e4m3fn UNET). It is a UNET-only weight, so it needs the SEPARATE
FLUX encoders DokiDex never shipped (its bases are Z-Image/SDXL/flux-2-klein — none provide FLUX.1's t5xxl), all also
ungated: `comfyanonymous/flux_text_encoders` → `t5xxl_fp8_e4m3fn.safetensors` (4.9 GB) + `clip_l.safetensors` (246 MB),
and `Kijai/flux-fp8` → `flux-vae-bf16.safetensors` (168 MB) as the VAE. Real base footprint **~17 GB**, comfortably
inside 32 GB — heavier than InstantID's **zero**-new-base reuse, but no longer a hard blocker. License is still
**FLUX.1 [dev] Non-Commercial** (free/ungated download, commercial use restricted — the same posture DokiDex already
accepted for dev-license models). **(2) Node = `balazik/ComfyUI-PuLID-Flux`, Alpha + stale.** It self-describes as
"Alpha version V0.1.0 … a prototype"; **last commit 2024-10-03** (~20 months stale, predates the current ComfyUI/torch),
so it may not LOAD on a 2026 ComfyUI without patching — genuinely weaker than the maintenance-mode cubiq InstantID node.
The only fork (`sipie800` Enhanced) is **FORMALLY DISCONTINUED 2025-10-07** — NOT used. **(3) Net-new weights** beyond
the FLUX base + the shared antelopev2 are small: `pulid_flux_v0.9.1.safetensors` (1.14 GB, `guozinan/PuLID`, ungated) +
the EVA02-CLIP the node auto-downloads on first run (~600 MB).

**Shipped (the gated install only):** a **`-Pulid`** sidecar switch on `setup.ps1` (mirrors `-FaceId`/`-InfiniteTalk`)
that clones the balazik node into `custom_nodes\ComfyUI-PuLID-Flux`, pip-installs `facexlib insightface onnxruntime-gpu`
(the **-gpu wheel ONLY** — same EP-namespace clash as the InstantID block; `insightface` is a no-op if `-FaceId` ran),
`Get-Model`s the non-gated FLUX fp8 base (unet → `diffusion_models\`, t5xxl/clip_l → `clip\`, ae → `vae\`) + the
pulid_flux weight (→ `pulid\`), and **SHARES InstantID's antelopev2**: the balazik README target is EXACTLY the same
`models\insightface\models\antelopev2` path `-FaceId` populates, so the **same `glintr100.onnx` sentinel** SKIPS the
download when `-FaceId` already ran (no re-download across the two paths; AST-pinned). Ergonomic alias:
`doki gen -Pulid -InitImage <face.png>` → `Resolve-GenKind`→`pulid`→`comfyuicustomworkflow=PuLID` (the reference face
rides the **init-image** channel, same as `-FaceId`); `-Pulid` requires `-InitImage` (fails loudly up front like `-Edit`).
An **AST-driven `setup-helpers`** block pins the node clone + the 5 non-gated weight URLs + a conservative
all-in-one negative — setup never pulls from `black-forest-labs/FLUX.1-dev` (hard-gated) **or** the
`Comfy-Org/flux1-dev` all-in-one (license-restricted-but-scriptable, just a poor 32GB fit), keeping the base on
the fp8 split-files path — + the antelopev2 sentinel-guard; `doki-gen` pins the new
kind/recipe + the `-InitImage` requirement + the IP-Adapter-flag negative.

**Decision-rule branch taken: base + node + weights all verify with concrete NON-GATED resolve URLs → WIRE the gated
`-Pulid` install; the runnable workflow JSON is deferred on-GPU (no blind JSON).** balazik's `examples/`
(`pulid_flux_16bit_simple.json` etc.) are ComfyUI **UI-graph** exports (top-level `nodes`/`links`/`groups`), NOT
SwarmUI's flat API-prompt `CustomWorkflows` format, so the runnable `media-assets\PuLID.json` is the **on-GPU authoring
step** (convert UI-graph → API-prompt, repoint to the fp8 base + the separate t5xxl/clip_l/ae, inject the SwarmUI
`${prompt}` + the reference-face init-image placeholder). Until then the `-Pulid` block installs node+weights and the
workflow copy is **guarded by a `Test-Path` that Warns** (identical to the Foley/InstantID/InfiniteTalk copies).
**On-GPU LABELED confirms (unprovable at rest):** (1) the Alpha/stale balazik node **even LOADS** on the current ComfyUI
(verify this FIRST, before authoring the workflow); (2) face-ID **render quality** + the **32GB fit** of the fp8 base +
the three encoders; (3) the antelopev2 contents (shared with `-FaceId`, community mirror, not byte-verified). **Every
existing path is byte-for-byte unchanged** — the `-FaceId`/InstantID block + its antelopev2 are untouched; the `pulid`
kind/recipe is purely additive; no default/URL/catalog row changed.

**Update — InfiniteTalk audio-driven talking-video SHIPPED as INSTALL-WIRING ONLY (workflow JSON deferred to on-GPU authoring).**
Mirrors the InstantID posture exactly, with one new wiring axis (audio) and one large new cost (an ~82GB base).
**Node:** the real ComfyUI integration is **Kijai's `ComfyUI-WanVideoWrapper`**, NOT a standalone MeiGen node — the
MeiGen-AI/InfiniteTalk `comfyui` branch is itself "based on ComfyUI-WanVideoWrapper" (verified). Its requirements
are heavier than InstantID's (accelerate/sageattention/...), installed via the same 3-candidate comfy-python probe.
**Weights, three groups (all HF-tree-verified):** (A) the **InfiniteTalk fp16 ADAPTER** — Kijai's repackaged
single-file form `Wan2_1-InfiniTetalk-Single_fp16.safetensors` (5.13GB; the upstream **"InfiniTetalk" typo is REAL**
and copied byte-for-byte) + the optional multi-person variant; the only genuinely new SMALL file (the official
MeiGen repo is a 169GB tree, not pullable whole; no fp8 adapter exists on Kijai's tree — fp16 only). (B) the
**chinese-wav2vec2-base** audio encoder (`TencentGameMate`, the HF/transformers `.bin` path — the 1.14GB fairseq
`.pt` is skipped). (C) **THE ~82GB BLOCKER** — the **Wan2.1-I2V-14B-480P** base (7 diffusion shards ~65.6GB +
UMT5-xxl TE 11.4GB + open-clip ViT-H 4.77GB + VAE 508MB). **CRITICAL DISK-STATE FINDING:** DokiDex ships Wan **2.2**
(5B ti2v + A14B-T2V GGUF + VAEs) but NOT the Wan2.1-I2V-14B base, and the 2.2 models do **NOT** substitute —
InfiniteTalk's adapter injects into that specific 2.1 14B UNet. So `-InfiniteTalk` pulls an ~82GB NEW base that
**dwarfs every other DokiDex weight** (flagged in the setup header exactly like the InstantID note flags PuLID's
missing FLUX base). An **fp8/GGUF Wan2.1-I2V-14B repack** would shrink it (the Kijai wrapper supports fp8 Wan I2V
bases) but that exact file was not verifiable at rest — it is an on-GPU sourcing step.
**Decision-rule branch taken: install URLs verified, but NO authoritative SwarmUI-API workflow JSON is sourceable ->
wire ONLY the gated install + the kind alias + this note; do NOT commit a blind workflow JSON.** Kijai's
`example_workflows` (`wanvideo_2_1_14B_I2V_InfiniteTalk_example_03.json`, ...) are ComfyUI **UI-graph** exports
(top-level `id`/`nodes`/`links`/`groups`), not SwarmUI's flat API-prompt `CustomWorkflows` format; the
MeiGen-AI `examples/` JSONs are **CLI inference configs**, not ComfyUI workflows at all. So the runnable
`media-assets\InfiniteTalk.json` is the **on-GPU authoring step** (load the UI-graph live -> convert to API-prompt
-> rewire base/wav2vec/adapter paths -> wire the image+audio inputs to SwarmUI's injection points), and until then
the `-InfiniteTalk` block installs node+weights and the workflow copy is a `Test-Path` Warn (identical to the
Foley/InstantID copies). Cite: `github.com/kijai/ComfyUI-WanVideoWrapper/tree/main/example_workflows`;
`github.com/MeiGen-AI/InfiniteTalk/tree/comfyui`.
**Ergonomics:** a new `infinitetalk` kind (`doki gen -InfiniteTalk`) -> `comfyuicustomworkflow=InfiniteTalk`,
requiring **BOTH** a portrait (`-InitImage`, fails loudly like `-Edit`/`-FaceId`) **AND** a driving audio clip
(`-Audio <wav/mp3>`, a NEW fail-loud guard). Audio is the one genuinely new input vs InstantID: a `[string]$Audio`
param threads `doki.ps1 -> Invoke-Gen -> Build-GenBody`, base64'd and parked under a **provisional `inputaudio`**
body key — the exact custom-workflow audio body-key is itself an on-GPU confirm (it only binds once
`InfiniteTalk.json` exists and names its audio-load node).
**FEASIBILITY — brutally honest: 32GB is marginal-to-tight and UNCONFIRMED, and meaningfully WORSE than every
existing DokiDex video path** (i2v rides Wan2.2-**5B**, Foley rides the lighter **Wan2.2-5B** fp16 base; InfiniteTalk
rides the FULL Wan2.1 **14B**). Native fp16 14B OOMs on 32GB before activations. The feasible path — **fp8 base + block-swap/StepSwap
offload** (DokiDex already leans on StepSwap for the Wan2.2-14B GGUF pair) **+ the wrapper's 81-frame / 25-overlap
chunking at 25fps** — is plausible but NOT guaranteed and can only be settled by a **live render**. So the WHOLE
feature is **on-GPU-gated**: node + ~82GB base + adapter installed at rest; workflow authored + 32GB fit + the
fp8-base sourcing + the audio body-key are ALL the labeled on-GPU confirms. **We do NOT claim it runs in 32GB.**
**PATH-ROUTING is one more on-GPU confirm:** the InfiniteTalk base TE/clip-vision/VAE land under the **raw ComfyUI
backend tree** (`dlbackend\comfy\ComfyUI\models\...`), NOT SwarmUI's own `Models\...` where DokiDex's other media
weights live. Whether SwarmUI bridges those two folder namespaces for the WanVideoWrapper node is unverified at
rest — the wrapper may specifically resolve against the ComfyUI tree via its own `folder_paths` (as the Foley node
does), so the placement is left as-is and the routing is flagged as a labeled on-GPU confirm (not changed blind).
**Every existing path is byte-for-byte unchanged** (the `infinitetalk` kind/recipe + the additive `-Audio` plumbing
are purely additive; no default/URL/catalog row changed).

**Update — LatentSync SHIPPED as the LIGHT lip-sync (`-LatentSync`), INSTALL-WIRING ONLY (workflow JSON deferred
to on-GPU authoring).** Mirrors the InfiniteTalk posture exactly, as the LIGHTER alternative to the shipped ~82GB
InfiniteTalk. **The pick is LatentSync (ByteDance), NOT MuseTalk** — the obvious "lightest on paper" hypothesis
(MuseTalk, ~4-6GB, MIT) does NOT survive node-maturity verification: both MuseTalk ComfyUI nodes
(`chaojie/ComfyUI-MuseTalk`, `AIFSH/ComfyUI-MuseTalk_FSH`) are abandoned since mid-2024 and predate MuseTalk 1.5
(they only run 1.0-era weights), and one face-parse weight is Google-Drive-gated — both install-maturity blockers.
**LatentSync is the ONLY light candidate with a maintained node running the model's current release.**
**Ranking (32GB single-user box; license + node maturity gating, not just VRAM):** (1) **LatentSync** — WIRED.
8GB VRAM (1.5) / ~18-20GB (1.6@512), best lip-sync fidelity here (latent-diffusion + SyncNet supervision); weights
**OpenRAIL++**, code **Apache-2.0** (commercially usable); node `ShmuelRonen/ComfyUI-LatentSyncWrapper` (951 stars,
pushed 2025-09-04, tracks 1.5->1.6) — **genuinely maintained**. (2) MuseTalk — lightest on paper but DEAD node
(see above). (3) EchoMimicV2 — Apache-2.0, ~16GB, more body-motion than crisp lip-sync, node less proven. (4)
Hallo2 — heavy/slow (90-120min/10min clip), immature node. (5) Sonic — **DISQUALIFIED** (CC BY-NC-SA, non-
commercial). (6) Float — **DISQUALIFIED** (CC BY-NC-ND + no ComfyUI node).
**Node:** `ShmuelRonen/ComfyUI-LatentSyncWrapper`, pip'd via the same 3-candidate comfy-python probe Foley/InstantID/
InfiniteTalk use (graceful Warn fallback). **Weights** ride the **PUBLIC** `ByteDance/LatentSync-1.5` repo
(OpenRAIL++; the 1.6 repo is intermittently gated/private per the wrapper README, so 1.5 is the safe default —
fits 8GB, leaves max 32GB headroom). Core runtime ~6.8GB (`latentsync_unet.pt` 5.07GB + `stable_syncnet.pt` 1.61GB
+ `whisper/tiny.pt` 75.6MB + the repo-root `config.json`) **+ the REQUIRED SD-VAE** (`stabilityai/sd-vae-ft-mse`
`diffusion_pytorch_model.safetensors` 335MB + `config.json`, into `checkpoints/vae/` per the wrapper README — LatentSync
is an SD-VAE-LATENT model and CANNOT run without it) + ~3GB auxiliary face/quality weights
(vit_g/vgg16/s3fd/sfd/2DFAN4/koniq/syncnet_v2/i3d) into `checkpoints/auxiliary/` (the wrapper also lazy-pulls some on
first run). **TOTAL ~9.8GB — roughly 1/9th of InfiniteTalk's ~82GB.** All sizes + the sd-vae-ft-mse resolve URL
HF-verified (the VAE resolve 302s to a public xet CDN — ungated).
**Decision-rule branch taken: install URLs verified, but NO authoritative SwarmUI-API workflow JSON is sourceable
-> wire ONLY the gated install + the `latentsync` kind alias + this note; do NOT commit a blind workflow JSON.**
The wrapper's `example_workflows/` are ComfyUI **UI-graph** exports (top-level `id`/`nodes`/`links`/`groups`), NOT
SwarmUI's flat API-prompt `CustomWorkflows` format — identical to the InfiniteTalk/PuLID/TtsSuite blocker. So the
runnable `media-assets\LatentSync.json` is the **on-GPU authoring step** (load the UI-graph live -> convert to
API-prompt -> rewire checkpoint/whisper/syncnet paths -> wire SwarmUI's video-input + audio-load injection points
-> validate a render), and until then the `-LatentSync` block installs node+weights and the workflow copy is a
`Test-Path` Warn (identical to the Foley/InstantID/InfiniteTalk copies). Cite:
`github.com/ShmuelRonen/ComfyUI-LatentSyncWrapper` ; `huggingface.co/ByteDance/LatentSync-1.5`.
**Ergonomics + the ONE I/O divergence vs InfiniteTalk:** a new `latentsync` kind (`doki gen -LatentSync`) ->
`comfyuicustomworkflow=LatentSync`, reusing the InfiniteTalk `-Audio` plumbing **verbatim** (just `latentsync` added
to the `-Audio` kind-allowlist; the provisional `inputaudio` body-key flows through unchanged, pinned on-GPU once
`LatentSync.json` names its audio-load node). **LatentSync is VIDEO-in re-sync** — it edits an EXISTING clip's mouth
to new audio, it does NOT generate a talking video from a portrait. So unlike InfiniteTalk it requires **`-Audio`
only** (the source video rides the workflow's own video-input channel; **no mandatory portrait `-InitImage`**). It
is **ADDITIVE, not a replacement** — InfiniteTalk (portrait->video generation) and LatentSync (video-in re-sync) are
**different jobs**; both stay.
**NO sharing with InfiniteTalk (honest):** LatentSync's audio encoder is **Whisper-tiny** (not InfiniteTalk's
chinese-wav2vec2), it uses **s3fd/2DFAN4** for face detect/landmarks (not antelopev2), and it is a self-contained
**SD-VAE-latent** model with **zero Wan dependency** — the ~9.8GB is all-new but TINY; there is no meaningful
re-download to avoid. **Every existing path is byte-for-byte unchanged** — the `-InfiniteTalk` block + the `-Audio`
plumbing are untouched; the `latentsync` kind/recipe + the install block are purely additive (no default/URL/catalog
row changed). **FEASIBILITY:** 8GB (1.5) fits 32GB with huge headroom (vs InfiniteTalk OOMing at native), but the
runnable workflow + node-load + a live render are the labeled on-GPU confirms — install-only at rest, same posture
as InfiniteTalk.

**Update — Nunchaku NVFP4 speed runtime SHIPPED as a gated `-Nunchaku` install + the verified NVFP4 model
variants (moved off the gated-follow-ups list above).** Nunchaku is **NOT a model** — it is a **SPEED RUNTIME**
(the `nunchaku-ai/nunchaku` pip wheel + the `ComfyUI-nunchaku` node; the org is **nunchaku-ai** — the old
`mit-han-lab/nunchaku` name is stale/redirects) that runs **NVFP4-quantized** model variants **~3x faster than BF16
on Blackwell/RTX-50xx**. Its value depends on whether a nunchaku NVFP4 variant exists for a model DokiDex runs.

> **CORRECTION (adversarial-review fix) — the original relevance analysis made two sourcing errors, now fixed:**
> **(A) Z-Image-Turbo DOES have a nunchaku NVFP4 variant — the original "NO MATCH for Z-Image / nunchaku has no
> Z-Image arch" claim was WRONG.** `nunchaku-ai/nunchaku-z-image-turbo` ships `svdq-fp4_r128-z-image-turbo.safetensors`
> (~3.91 GB, HF-tree + HEAD verified; also r32 + int4 ranks). nunchaku **v1.1.0** added Z-Image-Turbo 4-bit and
> **v1.2.0** a perf boost ("fuse qkv/norm/rotary for z image"); the pinned **v1.2.1** is after both. This is the
> **highest-value** NVFP4 add of the lot: Z-Image-Turbo is DokiDex's **#1 photoreal default + real-time-canvas base**,
> so this DOES accelerate the main draft path on Blackwell. **(B) FLUX.2 Klein NVFP4 is NOT a nunchaku model — it is
> BFL's OWN NATIVE FP4 checkpoint.** Its card cites native ComfyUI + Diffusers FP4 with **no** Nunchaku/SVDQuant (no
> `svdq-` prefix), and nunchaku's changelog has **zero** FLUX.2 entries — it loads via ComfyUI's native FLUX.2 FP4
> path, needing no nunchaku wheel/node. So Klein NVFP4 was **moved out of `-Nunchaku` into `-Models full`** (next to
> the plain Klein checkpoints) and **relabelled** "(native FP4, Blackwell)".

**Net result — three NVFP4 image weights, two of them nunchaku svdq, one native:**
(1) **Z-Image-Turbo NVFP4** (nunchaku svdq-fp4, `-Nunchaku`) — accelerates the default base; recipe = the Turbo band
(8 steps / cfg 1 / euler / simple). (2) **Qwen-Image NVFP4** (nunchaku svdq-fp4 non-Lightning base, `-Nunchaku`,
13.1GB) — base band 20/4/euler/simple. (3) **FLUX.2 Klein 4B NVFP4** (BFL native FP4, `-Models full`, 2.46GB,
**NON-GATED** — 302→xet CDN, no 401, unlike BFL's gated base FLUX.2-klein repo). (The famous Nunchaku "4.4s" number is
4090/int4 and does NOT transfer.)
**Decision-rule branch taken: nunchaku NVFP4 variants exist for DokiDex models (incl. the default base) AND the
Blackwell runtime install is authoritatively verified (v1.2.1, Jan 25 2026 — Grace Blackwell support landed v1.1.0,
NVFP4 is the v1.x target; GitHub-API-verified release matrix) → wire the gated install (wheel + node) + the verified
NVFP4 weights + catalog rows + the additive recipe overrides + graceful degradation. TDD.**
**Shipped (mirrors the Foley/InstantID/InfiniteTalk node+wheel sidecar):** a `-Nunchaku` switch on `setup.ps1` that,
in order (the node imports nunchaku at load, so **wheel FIRST**): (a) resolves the 3-candidate comfy-python, **PROBES
the live torch minor + `torch.version.cuda` + cpXYZ** (NOT hardcoded; nunchaku is a compiled C++/CUDA ext with no
fallback, so `torchX.Y` AND `cuXX.X` MUST match exactly) and pip-installs the matching `<cuTag>torch<tv>-<py>-win_amd64`
v1.2.1 **release-asset wheel** (`+`→`%2B`) — v1.2.1 ships **both** `cu12.8` (torch 2.8–2.11) and `cu13.0` (torch
2.9–2.11) matrices, so the CUDA tag is PROBED from `torch.version.cuda` (12.8→cu12.8, 13.x→cu13.0); **torch<2.8 has no
wheel → it Warns** (no 404 URL) and a cu13 env never gets a cu12.8 wheel; (b) clones `nunchaku-tech/ComfyUI-nunchaku`
+ its requirements; (c) under **`-Models full`** `Get-Model`s the two **nunchaku svdq** NVFP4 weights (Z-Image-Turbo +
Qwen) into `diffusion_models/` — wheel+node are **tier-independent** so a future NVFP4 model is one `Get-Model` away
even on `-Models lean`. Every step is `$cpy`/`Test-Path`-guarded with a `Warn` fallback (graceful degradation, same
posture as the other sidecars).
**Recipe override:** **Z-Image-Turbo NVFP4 = ONE additive line** (`svdq-*z-image-turbo.safetensors` → the Turbo band
8/1/euler/simple, dropping the BASE curated negative like the cfg-1 distill it is); **Qwen NVFP4 = ONE additive line**
(`svdq-*qwen-image.safetensors -and -notlike '*lightning*'` → the base band 20/4/euler/simple as the GGUF), keyed off
the svdq name so neither collides with the `.gguf`/`*base*`-discriminated Qwen-Edit path; the 4/8-step **Lightning** fp4
distills are explicitly **excluded** (they want cfg=1/low-step and are an on-demand add). **Klein NVFP4 needs ZERO
doki-gen.ps1 recipe change** — the existing `flux-2-klein*` glob already matches `flux-2-klein-4b-nvfp4.safetensors`,
and (no `-base-` infix) it takes the **distilled** branch (4 steps / cfg 1 / euler / Flux2); the negative-drop guard
fires too. **Caveat (now noted in code):** BFL's nvfp4 card states **no inference config** and does **not** label the
file distilled-vs-base (the `-base-` convention was Comfy-Org's repackage, not this repo), so the distilled routing is
the **conservative, on-GPU-unverified** call (a 4-vs-base-step A/B is the labeled confirm).
**Catalog:** three `model-catalog.json` rows (Z-Image-Turbo NVFP4 / Qwen NVFP4 / Klein native FP4) so all surface in
the Studio picker / SwarmUI's native picker / via `-Model`. URLs HF-tree / GitHub-API / HEAD verified; AST-driven
`setup-helpers` pins the node clone + the PROBED wheel-URL (now `$cuTag` + `torch.version.cuda` probe) + the weight
entries + a **Z-Image-Turbo-present assert** (the svdq z-image-turbo URL IS wired — the inverse of the old phantom
guard); `doki-gen` pins all three overrides + the additive/`-Kind`-guard contract. **Every existing path is
byte-for-byte unchanged.**
**On-GPU / LABELED confirms (render-unverified at rest — no GPU in CI):** (1) **which torch + CUDA build** the live
comfy env has → which wheel (PROBED, not assumed); (2) **whether SwarmUI loads a single-file nunchaku `.safetensors`
via its normal `-Model` picker or needs the node's own Nunchaku loader / a custom workflow** — if a workflow, the svdq
Qwen/Z-Image NVFP4 weights become a `-Workflow` hook (like Foley) rather than a bare `-Model` swap, which would change
the wiring (the one unknown that could turn the thin wire into authoring a workflow — resolve before promising the
additive-override path end-to-end); (3) the exact **NVFP4 step/cfg band** + the distilled-vs-base call on Klein at fp4;
(4) the **real Blackwell speedup** vs the bf16/GGUF path on this 32GB box.

**Update — TTS-Audio-Suite SHIPPED as INSTALL-WIRING ONLY (a GATED ALTERNATIVE; the per-engine workflow JSON deferred
to on-GPU authoring).** This was the LAST and MOST-DIVERGENT model-add on the gated-follow-ups list — a 15-engine TTS
node (`diodiogod/TTS-Audio-Suite`, code license **MIT**) that bundles ChatterBox/F5/VibeVoice/Higgs v2+v3/**IndexTTS-2**/
Step-Audio/CosyVoice3/Qwen3-TTS/MOSS/Granite/Echo/Dots + **RVC** voice-conversion. The architecture decision the
2026-06-18 note flagged is now **resolved and recorded**.

**ARCHITECTURE — it is a GATED ALTERNATIVE, NOT a replacement for the `:8004` speech path.** DokiDex's TTS is the
**standalone Chatterbox server** (`devnen/Chatterbox-TTS-Server`, installed by `-Tts`) on `:8004` — `control/Web/Tts.cs`
`Base=http://127.0.0.1:8004`, `POST /v1/audio/speech`, and the `/api/speak` endpoint Chat P4 voice-readback reuses. It
lives in the **LLM/llama-swap group** and **COEXISTS WITH CHAT** (voice readback works while the coder model is loaded,
**no GPU-exclusivity**) — a **load-bearing property of the chat surface**. TTS-Audio-Suite is a **ComfyUI node** that runs
inside SwarmUI's **media group, GPU-EXCLUSIVE with the LLM on 32GB** (2026-06-13). So routing speech through it would
**evict the LLM to media mode just to speak a line** — a real ergonomic regression for the primary chat-with-voice use
case. Its incremental value (IndexTTS-2 duration/emotion, Higgs v3's 100+ langs, RVC voice-changer) is **quality/feature
niceties for an explicit opt-in flow**, not a missing capability — DokiDex already has uncensored zero-shot voice cloning
+ a pronunciation lexicon + multi-speaker dialogue concat over Chatterbox. **So `:8004` stays the coexisting default,
BYTE-FOR-BYTE UNTOUCHED** (no new branch in `SynthBytesAsync`, no engine param on `SpeakRequest`); the suite is an opt-in
**media-group alternative** the user picks by mode + flag.

**Decision-rule branch taken (the 2nd branch — same as InstantID/InfiniteTalk): node verified, but NO authoritative
SwarmUI-hook-ready workflow JSON is sourceable → wire ONLY the gated install + the kind alias + this note; do NOT
blind-author a workflow JSON.** The suite's `example_workflows` (the "🌈 IndexTTS-2 integration.json", "Higgs Audio v3
Integration.json", etc.) are ComfyUI **UI-GRAPH** exports (top-level `id`/`nodes`/`links`/`groups`/`config`) — VERIFIED by
fetching the raw IndexTTS-2 integration JSON — **not** SwarmUI's flat **API-prompt** `CustomWorkflows` format. Identical
wall to InstantID (`examples/InstantID_basic.json`) and InfiniteTalk (Kijai's `example_workflows`). So the runnable
per-engine `media-assets\TtsSuite-<engine>.json` (e.g. `TtsSuite-IndexTTS2.json` / `TtsSuite-Higgs.json`) are the **on-GPU
authoring step** (load the UI-graph live → convert to API-prompt → rewire the text/voice/output injection points →
validate a render). Cite: `github.com/diodiogod/TTS-Audio-Suite/tree/main/example_workflows` (UI-graph, not API-prompt).

**Shipped (the gated install only):** a **`-TtsSuite`** sidecar switch on `setup.ps1` (mirrors `-FaceId`/`-InfiniteTalk`/
`-Nunchaku`) that clones the node into `custom_nodes\TTS-Audio-Suite` and pip-installs its requirements via the same
3-candidate comfy-python probe with the `$cpy`/else **Warn graceful-degradation** fallback. **No weights are pre-fetched:**
the suite's README states **ALL 15 engines AUTO-DOWNLOAD their own models on first node-use** (into the node's
`models\TTS\<engine>` convention), so `-TtsSuite` ships the node + its deps and lets each engine fetch its **complete,
current** file set lazily on the first `-Speak` that runs it. *(An earlier opt-in `-Models full` pre-fetch of **IndexTTS-2**
via `hf download IndexTeam/IndexTTS-2` and **Higgs v3** via a lone `model.safetensors` Get-Model off a `$hg`
`bosonai/higgs-audio-v3-tts-4b` base was **REMOVED**: it had two footguns — Higgs was **half-pinned** (only the 9.31GB
weight, not its `model.safetensors.index.json`/`config.json`/tokenizer siblings, so a sharded-aware loader could refuse a
lone file), and the IndexTTS-2 idempotency gate keyed only on `Test-Path gpt.pth`, so an interrupted multi-file pull looked
"complete" and never resumed. Auto-download-on-first-use eliminates **both** — there is no half-pinned/partial-pull risk and
no tier gate to reason about. Sizes are still license-noted in the setup header for awareness: IndexTTS-2 ~5.9GB, Higgs v3
9.31GB.)* **Ergonomic alias:** a new `speech` kind (`doki gen -Speak [-Engine <name>] '<text>' [-Audio <ref.wav>]`) →
`Resolve-GenKind`→`speech` → `comfyuicustomworkflow=TtsSuite-<engine>` (the engine selects WHICH workflow JSON runs).
The engine is normalized by **`Resolve-TtsEngine`**: strip non-alphanumerics **then case-fold through a known-engine table
to ONE canonical casing**, so `IndexTTS-2`/`IndexTTS2`/`indextts2`/`index tts 2` ALL collapse to the **same**
`TtsSuite-IndexTTS2` name (an unknown engine keeps its stripped, as-typed-case token, so the other ~13 engines still pass
through); default `IndexTTS2`. *(The prior `-replace '[^A-Za-z0-9]',''` did NOT case-fold, so `indextts2` routed to a
distinct `TtsSuite-indextts2` — a split-brain the doki-gen test now pins case-sensitively with `-ceq`.)* The text rides the
standard `${prompt}` injection point **verbatim** (NOT the `:8013` `<mpprompt:..>` rewriter — that would rewrite the spoken
words); an optional zero-shot reference voice clip rides **`-Audio`** on the same provisional `inputaudio` body-key
InfiniteTalk parks. A fail-loud `-Speak requires text` guard mirrors `-Edit`/`-FaceId`; the `-Audio` kind-guard now permits
**both** `infinitetalk` and `speech`. The workflow copy is **guarded by a `Test-Path` Warn** for `media-assets\TtsSuite-*.json`
(identical to the Foley/InstantID/InfiniteTalk copies). **An AST-driven `setup-helpers` block** pins the node clone + a
**removal guard that NO `hf` IndexTTS-2 pull / `$hg` Higgs var / lone `model.safetensors` Get-Model entry remains** (so the
footguns can't creep back) + a **CONTRACT guard that the `:8004` Chatterbox server is cloned exactly ONCE** (by `-Tts`) so
`-TtsSuite` adds no second/alternate Chatterbox path; **`doki-gen`** pins the `speech` kind/recipe, the per-engine
workflow resolution + the **canonical case-fold** normalization, the literal-`${prompt}` placement, the
`-Speak`-requires-text + `-Audio` guards, and a **doki.ps1 `-Speak`/`-Engine -BodyOnly` seam test**. **License caveats**
(accurate, not the brief's assumed labels) are
printed in the setup header: **IndexTTS-2** = bilibili **INDEX license** (PERMITS commercial use for individuals/small orgs;
separate license only above 100M MAU OR RMB 1B/yr; bars using it to improve other models — **fully fine** single-user, the
LESS restrictive of the two); **Higgs Audio v3** = Boson **Research/Non-Commercial** (single-user/local OK; bars
non-consensual voice cloning); **Echo-TTS** = CC-BY-NC-SA; RVC/most others permissive.

**On-GPU / LABELED confirms (render-unverified at rest — no GPU in CI):** (1) the engines **auto-download** their weights
on first node-use into the node's `models\TTS\<engine>` convention (inferred from the suite's conventions; setup pre-fetches
nothing, so there is no half-pinned/partial-pull risk to confirm); (2) **whether SwarmUI's image/video-centric CustomWorkflow runner can host a
pure-TTS node returning a WAV at all** — its custom-workflow path may need an audio-output-node mapping that doesn't exist
out of the box; this is a **genuine risk the on-GPU step must settle BEFORE promising the `-Speak` route end-to-end**; (3)
the per-engine **audio-input/text injection node names** (pinned when the workflow is authored). **Honest verdict:** marginal
value for a single user, and the GPU-exclusivity regresses nothing only because we **did NOT** wire it into the coexisting
chat path — the right call per the repo's own discipline is **install-only + on-GPU-flag**, keeping `:8004` the default and
deferring the workflow wiring rather than blind-authoring it. Suite green throughout (PS doki-gen **237→243**, setup-helpers
**104→108** — the close-review pass dropped the fragile IndexTTS-2/Higgs weight pre-fetch in favour of the node's
auto-download and added a canonical case-fold to the engine normalization; C# **456** unchanged — no C#/Tts.cs/api-speak/chat
change); Debug+Release `--no-incremental` clean. **Every
existing path is byte-for-byte unchanged** (the `speech` kind + the `-Speak`/`-Engine` plumbing are purely additive; no
default/URL/catalog row/`:8004` behaviour changed).

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
agentic-coder GGUF") has fired, and the pinned llama.cpp b9616 (2026-06-12) already supports
the qwen35moe / deepseek2 (GLM-4.7-Flash MLA, *not* glm4moe) / Qwen3-VL archs, so they're servable with no upgrade. These are
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
