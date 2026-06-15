# DokiGen Studio — design (proposal)

**Status:** proposed (2026-06-15). Not yet built — awaiting review. The biggest *easier-to-use* win from
the Sora 2 / Fable 5 UX research (`memory: dokidex-ux-sora-fable-research`): turn the `doki gen` CLI into a
visual in-panel surface, the way Sora's *app* makes video generation feel easy.

## Goal

A **Studio** page in the WPF control panel: type an idea → pick a kind → **Generate** → see the result
*inline* → **Remix** to refine. No CLI, no remembering flags, results visible immediately. It wraps the
existing, tested `doki gen` recipes — this is a *surface*, not new generation logic.

## UX (Sora-borrowed patterns)

A third left-rail nav item **Studio** (beside Dashboard / Logs), hosting one page:

- **Compose bar (top):** a large prompt box ("describe what to generate…") · a **kind** selector
  (Image · Video · Music · Edit · I2V · Foley) · modifier toggles (Fast, Upscale, Raw) ·
  an **✨ Generate** button. For Edit/I2V, a "pick image" affordance feeds `-InitImage`.
- **Preview (center):** the generated **image inline** (an `Image` bound to the artifact). Video/music
  show a frame/placeholder + **Open** (inline `MediaElement` is deferred — see phases).
- **Tray (below):** status (`generating… / done / error`), the artifact path, **Open** · **Save** ·
  **Remix** (re-run with a new seed or a small prompt tweak — Sora's *iterate-to-good*, not re-roll).
- **Media-mode guard:** if the GPU isn't in media mode, a calm banner "Switch to MEDIA to generate"
  with a one-click switch (reuses `MainViewModel.SwitchMode("media")` + its eviction confirm).
- The always-on `:8013` rewriter already expands lazy prompts; tuning it toward Sora-style *structured*
  prompts (subject/action/camera/timing/audio) is a parallel, separate improvement.

## Architecture (reuses what exists)

- `Views/StudioView.xaml` + `ViewModels/StudioViewModel.cs` — a page hosted in `PageHost`, exactly like
  `DashboardView` / `LogsView`. Nav: a `Studio` button in `MainWindow.xaml` + a `NavStudio` handler
  mirroring `NavDashboard`/`NavLogs`/`SetNav`.
- `DokiService.RunGenAsync(prompt, kind, mods, outPath, ct)` — shells
  `doki gen "<prompt>" -<Kind> [-Fast] [-Upscale] [-InitImage <p>] -Out <temp> -NoOpen`, captures the
  artifact path (the `[gen] -> <path>` line, or just the `-Out` target). GPU-gated (needs media mode);
  arg-building is pure + unit-testable.
- `StudioViewModel`: `PromptText`, `SelectedKind`, `Fast`/`Upscale`/`Raw`, `InitImagePath`,
  `IsGenerating`, `StatusText`, `ResultPath`, `ResultPreview` (a `BitmapImage` for images),
  `GenerateCommand`, `RemixCommand`, `OpenCommand`, `SaveCommand`. Reuses `OpenArtifact` (already guards
  http/.wav/local files).
- **Design-mode sample:** `SampleData`-style canned prompt + a placeholder preview so `--design` /
  `--render` shows the Studio populated (and snapshot-able) with no backend.

## Build phases (incremental + verifiable)

1. **Shell + nav + design sample** — the layout, VM, nav wiring, and a design-mode populated state.
   GPU-free; **render-verified** via `--design` / `--render`. *(First committable slice.)*
2. **Gen wiring** — `DokiService.RunGenAsync` + the Generate command → live gen → inline image preview.
   Unit-test the arg-building; the live run is verified in a media-mode session.
3. **Remix / refine** — keep the seed, tweak prompt/strength, converge.
4. **Inline video/music preview** (`MediaElement`) + Save-to-folder.

## Rationale & trade-offs

- **Why:** media gen is the hardest part of DokiDex to discover/use today (CLI-only). Sora's prompt→preview→
  remix loop is exactly what makes generation feel easy; the panel already gives us the surface, the
  design-mode/render harness for verification, and `doki gen`'s recipes for the engine.
- **Trade-offs:** live gen needs media mode (the guard handles it); inline *video* preview is deferred to
  phase 4 (WPF `MediaElement`/codec weight) — Open-externally for v1; the Studio adds a 3rd nav page
  (fits the existing rail).

## Verification

`--design` renders every Studio state (idle / generating / result / media-mode-off) → snapshot;
`StudioViewModel` arg-building + state transitions unit-tested (xUnit, like `MainViewModelTests`); the live
gen verified once in a media-mode session (folds into Phase-3 live verification).
