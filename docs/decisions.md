# Decision log

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
