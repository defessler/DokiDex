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
32GB — `doki` enforces the mutual exclusion. Docs: `docs/wiki/8-image-and-video.md`.
