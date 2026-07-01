# CC-experience plan — v2 (2026-07-01, verification-hardened)

> **Goal (user):** a coding experience as close to the Claude Code CLI as possible; the app accurately manages
> all models & services (especially the claude-code-style/coder ones); the app serves as a discovery surface for
> what features exist and how they work.
>
> **Provenance:** v1 synthesized from three Fable-5 audit forks (CC-parity, model-management, discoverability).
> **v2 = v1 hardened by four Fable-5 verification forks:** (F1) llama.cpp/HF feasibility — both risky assumptions
> CONFIRMED against primary sources; (F2) Claude-Code ground truth vs code.claude.com docs; (F3) adversarial
> red-team (~85% sound; 1 protocol flaw, 2 spec bugs, seams verified on disk); (F4) 2026 landscape/direction —
> **bespoke `doki code` validated** (local native tool-calling is the field's #1 pain; our SEARCH/REPLACE-in-content
> architecture routes around it). Executed by **Sonnet 5 implementer agents**. Baseline: `feat/doki-code-cli`
> @ 36bc063 (PR #1), xUnit 890/890 green.

## Verdict

| Axis | Score | Summary |
|---|---|---|
| 1. CC-feel `doki code` | ~65% | Machinery shipped (loop/tools/edits/approval/undo/diffs/reflection); missing the daily-driver feel: streaming, orientation, context accounting, sessions, rule-based permissions. Two real bugs (0.1). |
| 2. Model management | ~30% coder / ~80% media | Coder models = manual GGUFs + hand YAML + hardcoded tiers; candidates/evals/`doki code` invisible in-app; media installs unhashed. |
| 3. Discovery | ~50% | Strong Home hub; docs unreachable in-app; ~12 dark features; no CLI help. |

**Direction (F4):** continue **bespoke** `doki code` as the integrated default; keep **Crush documented as the
power-user alternative** (it's already wired via harness/crush.json — costs nothing); treat Mistral's Vibe CLI as a
design reference. Model strategy → **three-way bake-off** (see G2).

## Execution protocol (hardened per F3-R1…R5)

- **ONE implementation agent at a time, globally** — never two agents on the shared working tree (F3-R1). Lanes
  are an *ordering* concept, not concurrency: alternate Lane-A and Lane-B dispatches to interleave progress.
- Dispatch: Sonnet 5, one leaf per agent, this doc's leaf spec verbatim + "MINIMAL focused change; make the edits;
  do NOT commit; report diff hunks + test count." TDD where the leaf has a pure seam.
- **Specs anchor on identifiers (element ids, function/class names), NEVER line numbers**; the parent re-greps
  anchors immediately before each dispatch (F3-R5).
- Parent verifies ground truth per leaf: `git diff` review + `dotnet test` (890+ stays green) + `dotnet build`
  (CLI Release for Lane A) → parent commits. **Escalation:** a leaf failing twice escalates one model tier with a
  failure report; never silently re-run.
- **Human gates:** **G1** — user verifies every catalog url+sha256 before URLs/hashes merge or any install runs
  (procedure: fetch `https://huggingface.co/<repo>/raw/main/<file>`, copy `oid sha256` + `size` verbatim; prefer
  ungated community repos — bartowski/unsloth/ggml-org; official `mistralai/*` may be license-gated). G1 gates only
  the *values* — manager code + fixture tests proceed while verification is pending (F3-R4). **G2** — GPU-live:
  the **three-way coder bake-off** (incumbent Qwen3-Coder-30B vs **Devstral-Small-2-24B** vs **Qwen3.6-35B-A3B**
  [already staged as `coder-candidate-a3b`], all vendor numbers distrusted equally, `evals/run-suite.ps1` ≥91%
  golden + zero tool-call flakes decides), streaming feel-check, warm-load smoke. **G3** — NEEDS-DESIGN items stay
  parked (§Deferred).

---

## Phase 0 — correctness first (Lane A)

**0.1 Bash robustness — S.** In `CodeAgent.RunBash`:
(a) **stderr deadlock (real bug):** stdout is drained to EOF before stderr; a chatty child fills the stderr pipe
and hangs *before* the 120s timeout. Fix by draining both pipes concurrently — **mirror the repo's own correct
pattern in `DokiService.CaptureFullAsync`** (F3).
(b) **cancellation:** honor `ct` — kill the child process tree (`Kill(entireProcessTree: true)`) and return
"(interrupted)". (c) Make the 120s cap a named constant; note a future env override (CC: `BASH_DEFAULT_TIMEOUT_MS`).
Accept: >64KB-stderr command completes; cancelled `Start-Sleep 600` returns promptly.

**0.2 One-shot scripting — S/M** (F2: CC's headless story is bigger than exit codes).
(a) Exit codes: `RunOneTurn` returns success (signature change, F3); `Main` returns **1** on error, 0 on success.
(b) **Piped stdin**: when stdin is redirected, read (bounded ~2MB) and append to the prompt (CC: `cat log |
claude -p "explain"`). (c) `--output-format json` printing `{result, tokens?, duration_ms}` for scripting; note
`stream-json` as a follow-on. (d) `-p --continue` composes once 1.4 lands (XS then).

## Phase 1 — the Claude Code feel (Lane A; value-ranked)

**1.1 Streaming content tokens — M. CONFIRMED FEASIBLE (F1):** llama.cpp streams OpenAI-style
`delta.tool_calls` fragments since b5497 (we run b9616); **Qwen3-Coder's XML tool format has a native streaming
parser with grammar enforcement** (PR #16932); llama-swap SSE proxying already proven in-repo.
New `LocalLlm.ChatToolsStreamAsync`: `stream=true` on `StreamHttp`; yield `delta.content` live; **accumulate
`delta.tool_calls` fragments index-keyed** (id/name once, arguments string concatenated), parse at stream end into
existing `ToolCall` records. **F1 adjustments:** (a) `delta.reasoning_content` (gpt-oss) is never appended to the
transcript — drop or display dimmed; (b) end-of-tool-calls = `finish_reason=="tool_calls"` **or** accumulated
fragments present at `[DONE]`; (c) keep the blocking `ChatToolsAsync` fallback on malformed accumulation + a
`--no-stream` escape hatch (F3-R2). Display: print tokens live; buffer SEARCH/REPLACE block bodies once
`<<<<<<< SEARCH` is seen mid-stream (blocks render as diffs at the gate); **add Esc-to-interrupt** alongside
Ctrl+C (F2: CC interrupts with Esc, keeping work done so far).
Accept: pure tests for the fragment accumulator + block-suppression state machine; feel-check = G2.

**1.2 Repo orientation — M** (three parts, one dispatch).
(a) Workspace instructions: first-found of **`DOKI.md` → `AGENTS.md` → `CLAUDE.md`** at the workspace root
(~8k cap; F4: AGENTS.md is the cross-tool standard; F2: CC itself reads CLAUDE.md — first-found covers both) as a
second **byte-stable** system message (conscious divergence from CC's user-message injection — preserves our
`--cache-reuse` prefix; falls out free: instructions survive compaction).
(b) Preamble in the same message: depth-2 directory tree (~1-2k tokens; reuse `CodeTools` skip-dirs; show counts
for pruned dirs) + **bounded `git status -s`** (F4: Vibe does both).
(c) `/init`: runs the normal loop with a fixed prompt — "read any existing DOKI.md/AGENTS.md/CLAUDE.md first and
**improve rather than overwrite** (F2); explore with Read/Grep; Write a concise DOKI.md: purpose, layout,
build/test commands, conventions." Write's approval gate applies.
Accept: pure test for tree caps; instructions message byte-stable across turns.

**1.3 Context accounting — M.** (a) Meter after each turn: est. tokens (chars/4) over `working`; show against a
**~32k healthy working set** (F4: local 30Bs degrade past ~32k in practice), with the 131k hard window noted —
amber >24k, red >32k. (b) **`/compact [instructions]`** (F2: CC accepts focus instructions — pass them into the
summarization prompt) + **auto-compact when est. exceeds ~40k**: summarize all but the system messages + last 4
turns via one `LocalLlm.ChatAsync` call into a `[session summary]` turn; print `(compacted N→M tokens)`. System
messages survive verbatim (cache prefix + instructions retention). (c) Tiny `/context` breakdown (system / tools /
history estimates) folded in (F2).
Accept: pure test for the survive-compaction selector; meter math test.

**1.4 Sessions — M.** Persist `working` after each turn to
**`%USERPROFILE%\.doki\sessions\<workspace-hash>\<timestamp>.json`** — **outside the repo** (F2: CC stores
per-project transcripts centrally, never in the worktree; kills gitignore churn). `--continue` = most recent for
this workspace; **`/resume`** lists sessions (id + first-prompt snippet) and loads by number (`/sessions` alias);
`/export [file]` writes the transcript as markdown (F2, folded in). **Spec wrinkle (explicit):** `working` holds
anonymous objects — persist raw JSON, reload as `JsonElement` entries (LocalLlm re-serializes transparently).
Accept: round-trip test comparing **serialized request BYTES** (F3-R3) + a load-session one-shot smoke.

**1.5 Permissions, CC rule syntax — S/M** (F2: flat per-tool "always" is materially off CC — upgrade now, not later).
Persist **rule strings** to `%USERPROFILE%\.doki\permissions\<workspace-hash>.json`:
`{"allow": ["Read", "Bash(dotnet test *)"], "deny": ["Read(*.env)"]}`. Semantics: exact tool name, or
`Tool(prefix *)` trailing-star prefix match (Bash first-two-words offered), gitignore-style glob for Read/Edit
specifiers optional-v1. **Deny beats allow; deny is checked before the approval gate even runs** (fits the
security posture — e.g. `Read(*.env)`). On `a` for a Bash action, offer: `[a]lways this exact command /
[p]refix rule "<first two words> *" / tool-wide`. **`/permissions`** lists + edits (`/allow` alias).
Accept: pure rule-matching tests (exact, prefix, deny-wins).

**1.6 Usage + status — S.** Capture `usage` (prompt/completion tokens) from both blocking and streaming paths
(try `stream_options: {include_usage: true}`; if absent fall back to wall-clock t/s + blocking-path counts — F3).
Accumulate per session; **`/usage`** prints totals + last-turn tok/s (**`/cost`, `/stats` aliases** — F2); dim
` (12.4s · 38 tok/s)` after each turn. **Folds in `/status`** (F3 merge): llama-swap reachable?, loaded model,
configured tiers — one compact block.

**1.7 Input ergonomics — S.** (a) `@rel/path` mentions: validate via `ResolveWorkspacePath`, inline a bounded Read
window (max 3 files); unknown paths noted inline. (b) **`!command` shell passthrough** (F2: CC runs it directly —
the user typed it, so no model round-trip and no approval gate; still the bounded `RunBash` executor; result
appended to `working` as `[shell]` context).

**1.8 `/plan` mode — S** (F3 + F4 convergent add; the one real CC-core feature v1 missed).
`/plan` switches the profile: only Read/Grep are sent in the tools schema (schema-level filtering — the
harness-research §2.3 pattern; Cline/Vibe-proven) + one system line "plan mode: explore and propose; do not
change anything." `/act` (or `/plan off`) restores. Banner shows the mode. Todo *display* stays deferred.

**1.9 Custom slash commands — S/M** (F2 omission; a hallmark of CC's feel). `.doki/commands/<name>.md` files =
prompt templates; `/name args` expands the file (with `$ARGUMENTS` substitution) into the user turn. `/help` lists
discovered commands. Workspace-local first, then `%USERPROFILE%\.doki\commands\` global.

**1.10 Web + memory tools, opt-in — S/M** (F4: closes the asymmetry — Crush gets the stack's memory+websearch MCP
servers while `doki code` has no web/memory at all; CC has WebSearch). Add `web_search` and `memory_recall` tools
**behind a toggle** (`/tools web on|off`, default OFF — preserves the small-tool-set discipline), reusing the
existing `ChatTools.RunWebSearch` / `MemoryRecall` executors. Read-only ⇒ no approval gate.

## Phase 2 — coder-model management (Lane B)

**2.1 LLM model catalog (data) — S + G1.** `media-assets/llm-model-catalog.json`: entries
`{id, role(coder-fast|coder-big|reasoning|vision|fim|embed|candidate), files: [{file, url, sha256, size}],
sizeGb, llamaSwapModel, notes}` — **`files` is an ARRAY (multi-part GGUFs: gpt-oss-120b = 3 parts, each with its
own sha256)** (F1). Entries: the 4 live tiers, fim (qwen2.5-coder-3b), embed (nomic), and candidates
**Devstral-Small-2-24B AND Qwen3.6-35B-A3B** (F4). Agent drafts from llama-swap.yaml + setup.ps1 + HF raw
pointers; **user verifies every url+sha256 at G1 before merge**.

**2.2 LlmModelManager backend — M.** Mirror `ModelManager`: list (present/missing per file, using catalog `size`
for a **disk-space pre-check**), install (download → `.part` → **byte-count check → SHA-256 verify → move**, else
delete+error; per part), delete. Endpoints `GET /api/llm-models`, `POST /api/llm-models/{id}/install`,
`DELETE /api/llm-models/{id}`. Code + fixture-based unit tests proceed before G1 (tests use fake hashes); only
live installs need verified values.

**2.3 Models view "Text models" section — S.** Role badge, on-disk state, size, install/delete wired to 2.2;
candidate rows badged "bake-off — gate before promoting".

**2.4 Tier visibility + warm-load — M (pre-authorized split: endpoint leaf + UI leaf if the agent struggles).**
`/api/llm/tiers` returns `{tier, model, configured (llama-swap /v1/models), onDisk (2.1 catalog), loaded}` joining
`LlmTiers` + `StatusProbe.ConfiguredModels` + catalog. `POST /api/llm/warm {model}` wraps
`DokiService.WarmLoadModel` — **which is fire-and-forget `void`: the endpoint returns 202/accepted and the
existing 2s status poll surfaces the result** (F3). UI: compact tier table (Models or Status view) with Warm
buttons; chat tier select gains per-tier `title` explainers.

**2.5 Eval-gate badges — S/M.** `evals/results.jsonl` is **per-task rows** `{ts,harness,model,task,pass,seconds,note}`
(F3) — **aggregation rule: group by model → keep the most recent run per task → passCount/total**; join to catalog
entries via `llamaSwapModel` (rows use tier names). Badge in 2.3/2.4 tables: "golden 14/15" or "ungated".

**2.6 Sidecar model visibility — S.** Status cards show each sidecar's model (fim/embed/tts/stt): static model
names on `ServiceRegistry` defs → status payload → both UIs. *(Dispatched early — independent of the catalog.)*

**2.7 Hash-verify media installs — S + G1.** Optional `sha256` per `media-assets/model-catalog.json` entry +
verify-before-promote in `ModelManager.DownloadAsync` (delete + clear error on mismatch; hashless entries keep
current behavior + log a warning). Hashes verified incrementally at G1.

**2.8 HomeCatalog truthing — S.** Wire `SnapshotFrom.ModelsPresent` as the **union of media (ModelManager) and
LLM (2.2) presence** (F3: media-only would leave the LLM readiness gates dead); verify the Models-card blurb
("image / video / LLM models") is now true post-2.3 and adjust wording if needed.

## Phase 3 — discovery surface (Lane B)

**3.1 `doki code` Home card — S.** New "Code" group card in `HomeCatalog.Capabilities`: blurb, **readiness =
agent mode up + llama-swap healthy (NOT model presence — F3)**, 3-step guide (`doki up agent` → `cd project` →
`doki code`), starters: copy-command + open-docs. One-line mention on the Status view.
**3.2 In-app Help view — M.** `GET /api/docs` (list) + `GET /api/docs/{name}` serving whitelisted `docs/*.md` +
`docs/wiki/*.md` (path-gated, read-only) + a "Help" nav view with a minimal markdown renderer. **Security (F3):
the renderer must escape ALL HTML — build DOM via textContent/`esc()`, never innerHTML of raw markdown** (an
unsanitized renderer over repo files is an XSS foot-gun).
**3.3 `doki help` — S.** `help` command + bare-`doki` hint + ValidateSet failures point at it: command → one-liner
table covering everything incl. `code`; **mentions Crush as the wired power-user alternative CLI** (F4 hybrid).
*(Dispatched early — independent of the catalog.)*
**3.4 Dark-feature cards/starters — S.** HomeCatalog entries for Training, Compare, Batch/CSV, Series, Pitch-deck,
Inpaint/SAM with guides + starters; unhide/label the hidden Compare button.
**3.5 Command palette (Ctrl+K) — M.** SPA action registry `{name, synonyms, action}` covering views + starters +
dark features; overlay with fuzzy filter; reuses `setView`/starter plumbing. Registry contents spec'd from 3.4.
*(3.6 per-view popovers CUT to post-re-audit — redundant with Home guides + Help + palette unless the re-score
shows a residual gap.)*

## Dispatch order (single-agent, alternating lanes)

**A:** 0.1 → 0.2 → 1.1 → 1.2 → 1.3 → 1.4 → 1.5 → 1.6 → 1.7 → 1.8 → 1.9 → 1.10
**B (interleaved):** 2.1-draft → 2.6 → 3.3 → 2.2 → **[G1]** → 2.3 → 2.4 → 2.5 → 2.7 → 2.8 → 3.1 → 3.4 → 3.2 → 3.5
Every leaf: verify (tests+build+diff) → commit → next. **Re-audit + re-score the three axes after Phases 1–3.**

## Decisions recorded (so they stop resurfacing)

- **GBNF/json_schema tool-arg grammar: folded, not added** (F3) — native `--jinja` templates grammar-constrain tool
  sections on b9616; 1.1's malformed-accumulation→blocking-retry IS the retry loop; Stage-1 arg preservation +
  tight sampling removed the residual risk.
- **setParams server-side aliases: out** — `doki code` sends sampling client-side; server enforcement only helps
  non-DokiDex clients. **remaining_budget signal: superseded by 1.3.**
- **Crush stays wired** as the documented power-user alternative (hybrid, F4); Vibe CLI = design reference only
  (Mistral-API-only, can't talk to llama-swap).

## Deferred (G3 — do NOT dispatch)

MCP client proper (1.10 covers the immediate web/memory asymmetry; revisit for filesystem/git servers) · hooks ·
`/rewind`-style checkpoint timeline (single-step `/undo` + git covers v1) · CC-style auto-memory · todo/plan
display · subagents (low value-per-token on one GPU) · full tool-call-delta *rendering* · plan-execute two-model
routing (reasoning tier plans / coder executes — research-harness §2.2) · serving perf batch (coder-big `-b 4096`,
TTL tuning, MTP — all G2) · **StatusProbe.ActiveGroup misreport** (first-running-service order → wrong during
mixed transitions; needs a "switching" state design) · web-chat code-mode + onboarding tour (user-deferred) ·
per-view popovers (post-re-audit) · walk-up/imports for instructions files.

## GPU-gated (G2 — user runs with the stack up)

Three-way coder bake-off: **Qwen3-Coder-30B (incumbent) vs Devstral-Small-2-24B vs Qwen3.6-35B-A3B** — eval gate
(`run-suite.ps1` ≥91% golden + zero tool-call flakes) + a real `doki code` task each; streaming feel-check (1.1);
warm-load smoke (2.4); Stage-1 sampling live-check (`test-toolcall.ps1`).
