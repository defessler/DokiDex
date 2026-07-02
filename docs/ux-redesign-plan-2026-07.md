# UX redesign plan — the 1-stop shop (2026-07-01)

> **Goal (user):** "make the DokiDex app more useful — it has poor UX, is clunky and confusing. I want it to be
> my 1-stop shop for managing all of code, image gen, chat, etc."
>
> **Provenance:** two Fable-5 planner forks — (1) a live UX audit (headless screenshots + real API payloads +
> IA/flow analysis of the running app) and (2) a target-architecture study grounded in the in-repo UX research
> (ai-site-ux-scan, the chat-surface spec) and the shipped codebase. Their findings converge. Implementation =
> Sonnet-5 leaves, one agent on the tree at a time, parent-verified, per the cc-experience-plan protocol.

## Diagnosis (from the live audit — why it feels clunky)

**The IA mirrors the git history, not the user's jobs.** Every shipped feature became a peer tab: 14 flat
destinations fronting what are really four jobs (Code / Create / Chat / Manage). **The one-GPU constraint leaks
into the UX** as developer vocabulary (`agent/coexist/media`), confirm dialogs, guard banners, and *asymmetric*
auto-flips (chat round-trips flip both ways; Create strands you in media mode). **And the 1-stop-shop premise is
broken at both ends:** the code pillar lives in a terminal (the app's Code card literally copies a command and
sends you away), while system management lives in a second app (the WPF panel) whose child window *is* the web app.

Top failures, ranked (full evidence in the audit): ① the media job shattered across 5 tabs + 8 Home cards;
② dev-vocabulary GPU modes + mid-flow confirm dialogs; ③ asymmetric auto-flip (stranded in media mode);
④ code not in the app; ⑤ **status lies** — the pill can read "GPU NONE" while 16GB and a model are loaded
(the ActiveGroup/pidfile misreport is user-visible, not theoretical); ⑥ the two-app split; ⑦ Create = an
88-control single form; ⑧ first-paint shows 11 "checking…" placeholders; ⑨ the chat→image round-trip — the
app's best flow — hidden behind an off-by-default toggle that silently disables streaming; ⑩ responsive breakage.

## Direction (target-architecture study; candidates A/B/C compared)

**Chosen: B — a four-destination shell — sequenced C-first, keeping chat's inline-generation powers.**
- **A (chat-centric everything-app)** rejected: buries the Create depth (ControlNet/LoRA/canvas — the actual
  differentiator) behind intent-routing on a 30B local model, the flakiest possible router. XL migration.
- **C (polish only)** rejected as the destination (doesn't deliver the shop) but adopted as the sequencing:
  friction fixes land first, layout second.
- **B:** nav = **Home · Create · Chat · Code · Library + one Manage gear**. Create absorbs Director/Flow/Scene/
  Cast/Voice as sub-tabs (re-parenting, not rebuilding). Code becomes a real web workspace over the existing
  `CodeAgent` core (terminal stays canonical — same engine, same sessions). A persistent global bar (human mode
  names + loaded model + queue count + one-click switch) replaces all guard banners. WPF panel untouched
  near-term; demoted to boot+logs once web Manage reaches parity.

## Phases (each leaf: Sonnet-5, minimal diff, parent verifies + commits)

### Phase 1 — kill the clunk (no layout change; attacks ②③⑤⑧⑨)
- **1.1 Truthful status — S/M.** Fix the ActiveGroup/running misreport the audit caught live (healthy llama-swap
  + loaded model must never render "GPU NONE"): derive `running` from health, `ActiveGroup` from healthy
  services + loaded model (prefer llm when a model is resident), add an explicit "switching…" state. This was
  deferred as NEEDS-DESIGN; the audit's concrete failing payload IS the design input. Unit-test the derivation.
- **1.2 One mode policy — M.** A single "needs-the-other-engine" component — **[Switch & run] [Queue for later]**
  — used by Create/Director/everything, backed by generalizing `ChatGenCoordinator` (queue → flip → render →
  **flip back**, symmetric everywhere). Delete the confirm dialog + per-view guard banners. Human mode names in
  ALL user-facing text: "Chat & Code" / "+ Autocomplete" / "Image & Video" (internal ids unchanged).
- **1.3 Streaming tools chat + round-trip default-on — M.** Wire `LocalLlm.ChatToolsStreamAsync` (built for the
  CLI) into the web chat's tools path so tools no longer cost streaming; then default the tools toggle ON.
  Surface "Render N queued" more prominently (global bar queue count links to it).
- **1.4 First-paint skeletons — S.** One status source feeding all cards; skeleton states instead of 11
  "checking…" strings.

### Phase 2 — the shell (attacks ①⑥⑦; pure re-parenting where possible)
- **2.1 Nav regroup — M.** Home · Create(+sub-tabs: Director | Flow | Scene | Cast | Voice) · Chat · Code(v0 =
  the sessions browser placeholder) · Library · **Manage gear** (Models | Status | Memory | Help as a drawer).
  Every existing `<section>` survives; `setView` gains sub-view support; palette/starters update.
- **2.2 Global bar v1 — S/M.** Mode pill (human names, truthful per 1.1) + loaded model + queued-gen count +
  one-click switch — persistent across views; kills the last banner.
- **2.3 Create progressive disclosure — M.** Essentials row (prompt · kind · aspect · Generate) always visible;
  everything else (seeds/LoRA/ControlNet/tile/init/live) in collapsed "More" groups. No control removed.
- **2.4 Responsive pass — S.** Fix the wrapping logo/nav overflow at narrow widths (bounded; desktop-first).

### Phase 3 — the Code workspace (attacks ④; the pillar)
- **3.0 SECURITY DESIGN GATE (G3, with the user).** Approval-over-HTTP for mutating actions: proposal = same
  deny-first rules engine (`CodePermissions`), approvals as pending records answered by the UI, localhost-only +
  the existing origin gate, plan-mode default. One page, user signs off before any code.
- **3.1 Backend — M/L.** `/api/code/*` over the existing `CodeAgent.RunTurnAsync`: SSE streaming (callbacks →
  event stream, reusing the accumulator), sessions list/read from the on-disk store, approval-pending store.
- **3.2 UI — L.** The Code view: sessions browser → transcript render (tool cards, diffs via
  `CodeEdit.RenderDiff`) → run-a-turn with plan-mode default → approval cards for mutations. **First tenant of
  the index.html modularization** (new JS lands as a module, not more monolith).

### Explicitly out (this plan)
Candidate A's intent-router · WPF panel rewrite (Manage-parity first, demotion later) · mobile-first work ·
in-app bake-off pipeline (separate design) · onboarding tour (revisit after Phase 2 re-audit).

## Order + gates
1.1 → 1.2 → 1.3 → 1.4 → [re-audit feel] → 2.1 → 2.2 → 2.3 → 2.4 → [3.0 user gate] → 3.1 → 3.2 → re-audit + re-score.
Every leaf: tests + build + diff review → commit. The Phase-1 block alone should measurably kill "clunky";
Phase 2 kills "confusing"; Phase 3 completes the shop.
