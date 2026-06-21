# DokiDex Studio — Guided "Home" command center (design spec)

**Date:** 2026-06-21
**Status:** Design. Two open choices were resolved with the recommended defaults in the user's absence (the autonomous loop kept running) — both are flagged below and trivially revisable.
**Surface:** the DokiGen Studio SPA (`control/Web/wwwroot/index.html`), the user-facing web app on `http://127.0.0.1:5111`. (The WPF control panel is the ops cockpit and is out of scope.)

## Motivation
DokiGen Studio packs **10 capability areas** (Create, Director, Chat, Cast, Voice, Flow, Scene, Library, Models, Status) behind terse one-word nav, with no onboarding, descriptions, or "what can I do / what's ready" guidance. It's powerful but doesn't teach itself. Goal: a guided **Home command center** that makes the whole toolset discoverable (*what you can do*) and shows what's ready to use right now (*what you can use*), guiding the user into each capability.

## Decisions (from the brainstorming dialogue)
- **Shape:** a guided **Home hub** (landing page) — not a tour or scattered hints (those can layer later).
- **Richness:** **full command center** — state-aware capability cards + recent-work + quick-start + live GPU/mode meter.
- **Teaching depth:** **clickable example starters** (launch an area pre-filled) **+ an expandable mini-guide** per area.
- **Architecture:** **catalog-driven** — one declarative capability catalog renders the cards; live widgets reuse existing data.
- **Grouping (default adopted):** cluster the 10 areas into **Make / Talk / Manage**.
- **Readiness (default adopted):** computed **server-side** (a `GET /api/home` joining the catalog with live status) so the ready/needs-X logic is C#-unit-testable and reuses the existing status / ServiceRegistry logic.

## The design

### View
A new `Home` view becomes the Studio's default landing screen (first nav item). All existing views are unchanged; the SPA's initial state becomes `setView('home')`.

### Layout (top → bottom)
1. **Header + GPU/mode meter** — title + current GPU group (Agent / Media / Idle), VRAM used, and a one-click mode switch (reusing the existing eviction-confirm switch — never bypass the 32 GB-headroom guard). Surfaces the chat-vs-media GPU exclusivity that's invisible today.
2. **Quick-start box** *(Phase 2)* — one input: a question routes to Chat; anything else to Create (kind selector, default image). The "just start typing" door.
3. **Capability clusters** (the heart) — the 10 areas grouped:
   - **Make:** Create · Director · Flow · Scene
   - **Talk:** Chat · Cast · Voice
   - **Manage:** Library · Models · Status
4. **Recent work** *(Phase 2)* — a row of latest Library thumbnails; click → Library / remix.

### Capability card
- **Blurb** — one line on what the area does.
- **Readiness badge** — `Ready` / `Needs Media mode` / `Needs Agent mode` / `Needs setup`, from the catalog's `requires` rule joined with live status (mode, services up, models installed). `Needs X mode` offers a one-click switch; `Needs setup` links to Models/Status with the how.
- **Starters** — 2–4 `{label, view, prefill}`; clicking calls `setView(view)` + prefills (reusing the existing remix/prefill path). Teaches by doing. E.g. *"a neon dragon over a rainy city" → Create(image)*, *"summarize a PDF" → Chat*, *"photo → 5-sec clip" → Create(i2v)*.
- **Mini-guide** *(Phase 2)* — a collapsed "how it works" (3–5 steps + a tip), expand-on-demand so the hub stays skimmable.

### Content model — the capability catalog
A single declarative structure (the source of truth). Per entry:
```
{ id, group: 'make'|'talk'|'manage', name, icon,
  blurb,                                       // one line
  requires: { mode?: 'agent'|'media', service?: <name>, model?: <relpath> },
  starters: [ { label, view, prefill } ],
  guide:    [ step, ... ] }                    // mini-guide steps (Phase 2)
```
Adding/editing a capability = one entry. The catalog is **content, not logic**; it lives server-side (C#) so `/api/home` can join it with status, with the rendered cards purely a function of it.

### Readiness (server-side)
`GET /api/home` returns the catalog entries, each annotated with a computed `readiness` (badge + next-step), by joining the catalog's `requires` with a live status snapshot (current mode, services up, models present) — reusing the existing StatusProbe / ServiceRegistry. The **readiness resolver** (`requires` + status snapshot → badge + next-step) is a **pure function**, C#-unit-tested. The Home view simply fetches `/api/home` and renders.

### Quick-start routing (Phase 2)
A pure function: input string → `{view, prefill}`. Heuristic: looks like a question (ends with `?`, or starts with who/what/why/how/when/where/can/should/is/are/does) → Chat; else → Create, with the kind inferred from keywords (`video`→video, `song`/`music`→music, else image). Pure → unit-tested. No LLM classifier in v1.

## Architecture / isolation
- **Catalog** (data) — content only.
- **Readiness resolver** (pure C#) — `requires` + status → badge + next-step. Unit-tested.
- **`/api/home` endpoint** — catalog + readiness, from the existing status probe.
- **Home view renderer** (SPA JS) — renders groups/cards/widgets from `/api/home`; starters reuse `setView`+prefill.
- **Live widgets** — recent-work (existing gallery endpoint), mode meter (existing status/mode).

Each unit has one clear job and a defined interface; the logic-bearing pieces (resolver, routing, catalog validation) are server-side and testable, the SPA renderer is thin.

## Phasing
- **Phase 1 (core — "see everything / what's ready / one click in"):** the `Home` view + `GET /api/home` (catalog + server-side readiness) + grouped capability cards + clickable starters + readiness badges + the GPU/mode meter; Home set as default.
- **Phase 2 (polish):** expandable mini-guides + recent-work thumbnails + the quick-start box.

## Testing
- **Readiness resolver** and **quick-start routing**: pure C# functions, xUnit-tested (like the rest of the stack).
- **Catalog validation** test: every starter `view` is a real Studio view; every `requires` references a real mode/service/model id.
- **SPA render**: manual/visual verification (the repo has no JS test harness — logic is deliberately kept server-side to maximize C# coverage; the renderer stays thin).

## Risks / open items
- **SPA JS** is outside the C#/python/ps1 test harness → mitigated by pushing logic server-side; the renderer is thin + visually verified.
- **GPU-mode UX** — the mode meter + "Needs X mode" must reuse the existing eviction-confirm switch, not a new GPU controller.
- **Build dependency** — the control-panel build / `doki test` / release currently need `pwsh` (uninstalled → exit `9009`); reinstall PowerShell 7 (`winget install Microsoft.PowerShell`). C# is verified this session via a `pwsh`→`powershell.exe` shim.
- **Adopted defaults** — Make/Talk/Manage grouping + server-side readiness were recommendations adopted to unblock in the user's absence; both are easy to change.
