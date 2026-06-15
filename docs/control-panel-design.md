# DokiDex Control Panel — Design & Build Plan

Build-ready design for the DokiDex control panel app, distilled from the multi-agent
design workflow (judged stack → UX spec → backend integration), with every environment
fact verified live on this box (2026-06-14).

## Decision: C# WPF on .NET 9

**Build: C# WPF (`net9.0-windows`), CommunityToolkit.Mvvm, a ported VS-Code-dark Fluent
theme, shelling out to `doki.ps1` and reading live data over HTTP / `nvidia-smi`.** No
embedded PowerShell runspaces, no web server, no separate backend process.

Why WPF won over (a) upgrading the `control.ps1` PowerShell-WPF app and (c) a local web UI
(ASP.NET minimal-API + Svelte):

- **Zero new toolchain, proven on this box.** .NET SDK 9.0.313 + `WindowsDesktop.App
  9.0.17` are installed (SwarmUI itself is a `.csproj` that builds here). `dotnet build` is
  a known-good path. Tauri is dead on arrival (no Rust / VS C++ Build Tools).
- **Single language, single build, best AI-buildability.** WPF/XAML +
  CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`) are
  the most heavily-represented desktop pattern in training data. No JS↔C# two-process
  debugging, no CORS/port/"is the backend up" states.
- **Native fit is the product.** A local single-box single-user cockpit: real Win32
  window, instant cold start, no browser tab, no localhost port to bind.
- **Live data is first-class.** `HttpClient` for health; `Process` for `nvidia-smi`;
  `FileSystemWatcher` incremental read for true log tailing; `PeriodicTimer` on a
  background task marshaled via the dispatcher. None of the single-STA runspace tax the
  current `control.ps1` pays (its `Start-Job` + 4 s poll).
- **`doki.ps1` stays authoritative.** The panel is a *reactive face*: it shells
  `doki up|down|start|stop|restart|status json`, so the control plane stays fully usable
  from the terminal and the GUI never forks the logic.

The runner-up (ASP.NET minimal-API + Svelte) is deferred to **v2, only if LAN/phone reach
is needed** — and the C# record shape below is deliberately REST-ready so it would not be
a rewrite.

> **Deviation from the workflow's recommendation:** the design proposed the **WPF-UI**
> NuGet for Mica/Fluent chrome. We ship v1 on **pure WPF + a ported dark theme** instead,
> to remove the one external-restore + unfamiliar-API risk in an offline-capable build.
> CommunityToolkit.Mvvm (source-gen only, no runtime cost, stable API) is the single NuGet
> dependency. WPF-UI Mica chrome is a clean later upgrade, not a v1 gate.

### Runtime dependencies

| Dependency | Status | Notes |
|---|---|---|
| .NET 9 SDK / `WindowsDesktop.App 9.0.17` | **present** | `net9.0-windows`, `UseWPF=true` |
| CommunityToolkit.Mvvm 8.x | **fetch (1 pkg)** | source-gen MVVM; restores from nuget.org |
| `nvidia-smi` | **present** | on PATH; verified `4749/32607 MB, util, temp, watts, fan` |
| `pwsh` 7.6 + `doki.ps1` | **present** | the backend |

## Architecture

```
DokiDex.Control (WPF, net9, single process)
  Views (XAML)          ViewModels (CommunityToolkit.Mvvm)     Services (plain C#)
  MainWindow      <───  MainViewModel                    ┌──► DokiService    (shell doki.ps1)
   ├ DashboardView <──   ObservableCollection<ServiceVM>  ├──► GpuService     (from status json)
   ├ LogsView      <──   GpuViewModel                     ├──► RegistryService(status json poll)
   └ status strip  <──   ModeSwitchViewModel              ├──► LogTailService (FileSystemWatcher)
  GpuMeter / ModeSwitcher / ConfirmSheet (controls)       └──► TestGenService (verify.ps1 calls)
            │ subprocess pwsh -File doki.ps1 …    │ HTTP localhost      │ via doki status json
            ▼                                      ▼                     ▼
      doki.ps1 ($Services/$Profiles, group exclusion, .run\*.{pid,log,log.err})  llama-swap/SwarmUI/...
```

### Backend = `doki.ps1` (authoritative) + live read sources

| Datum | Source | Cadence |
|---|---|---|
| health (healthy/starting/down) | `doki status json` → `healthy`+`running` per service | 2 s poll |
| GPU used/total/util/temp/watts/fan | `doki status json` → sibling `gpu` object (one `nvidia-smi` call) | 2 s poll |
| loaded model + menu (llama-swap) | `doki status json` → `model`/`modelState`/`configuredModels` | 2 s poll |
| pid / installed / port / ui / vramGB | `doki status json` per service | 2 s poll |
| log lines | `.run\<name>.log[.err]` via `FileSystemWatcher` + incremental read | live |
| version / update | panel's own winget/git/gh job (ported from `control.ps1`) | manual button |

Control (panel never re-implements logic — it calls doki):

| Action | Call |
|---|---|
| mode switch | `pwsh -File doki.ps1 up agent\|coexist\|media` (hidden) — `DoUp` stops the other group first |
| stop all | `doki down` |
| per-service start/stop/restart | `doki start\|stop\|restart <name>` (group-guarded) |
| open web UI | `Start-Process <ui>` from the service's `ui` field |
| test-gen | `TestGenService` issues the exact `verify.ps1` request per modality |

### Data contract (C# records mirroring doki's JSON — one source of truth)

```csharp
record ServiceStatus(string Name, string Group, string Desc, int? Port, string? Ui, int? VramGB,
    bool Healthy, bool Running, int? Pid, bool Installed,
    string? Model, string? ModelState, string[] ConfiguredModels,
    string Version, string Update, string[] Profiles);
record GpuStatus(int UsedMB, int TotalMB, int Util, int Temp, double Watts, int? Fan,
    bool PerProcess, string ActiveGroup);   // PerProcess=false on this WDDM driver
record StatusDoc(ServiceStatus[] Services, Dictionary<string,string[]> Profiles, GpuStatus? Gpu);
```

### Phase 0 — `doki.ps1` enhancements (DONE, committed)

`StatusJson` enriched with `pid`/`ui`/`vramGB`/`model`/`modelState`/`configuredModels` +
sibling `gpu` object; `port`/`ui`/`vramGB` added to `$Services`; `start`/`stop` wired with
the GPU group-guard; `restart <service>` and a `panel` verb added. Verified:
`doki status json | ConvertFrom-Json` shows the enriched shape incl. `gpu`.

## UX spec (the essentials)

Design thesis: **one glance = what's alive / what's loaded / how much GPU is left**; the
**32 GB single-occupant-group** reality is made visible so a mode switch never feels like a
gamble; **everything is data-driven off `$Services`/`$Profiles`** so a new service appears
as a card with zero UI code.

- **Three zones:** left rail (mode pill + GPU meter + nav + global actions) · content pane
  (Dashboard / Logs) · one-line status strip (last-action ticker + spinner).
- **Cards grouped by doki `group`** into labeled bands (LLM teal `#4ec9b0`, MEDIA amber
  `#d7ba7d`) with a 3px left accent; the **inactive band is recessed** (~55% opacity) so
  you see media is a different world before clicking.
- **Mode switcher** = segmented radio (pick-one), with a **hover preview** of the switch in
  the explainer box and a **predictive 32 GB headroom bar** (per-service `vramGB`
  estimates summed for the target mode; overflow turns red + disables the switch).
- **Eviction confirm sheet** only when switching *into/out of* media (it tears down loaded
  weights): two explicit stop/start columns + the reused headroom bar; default focus =
  Cancel. agent↔coexist (same LLM group) is a toast, no modal.
- **GPU meter** (the trust instrument): one stacked bar = the 32 GB card attributed to the
  active group (per-process VRAM is `[N/A]` on this driver — honest aggregate, not fake
  per-service segments). Headroom is a first-class number; amber < 2 GB; temp amber > 80°C.
- **Live logs:** per-service tabs built from the registry, "All" merge with colored
  prefix, regex/substring filter, severity color (WARN amber, ERROR red, swap/model teal),
  pause + "↓ N new" pill, stderr toggle (`.log.err`); a degraded card's [logs] deep-links
  pre-filtered to errors.
- **Per-modality ⚡ test** fires the smallest real gen per modality (exact `verify.ps1`
  calls) into a result tray; disabled with a tooltip when the service's group isn't active.
- **State vocabulary** (dot + glyph + label + motion): healthy ● teal · stopped ○ grey
  (recessed) · starting ◐ blue pulse · swapping ◐ amber rotating · degraded ◍ orange
  (floats up + [logs]) · crashed ✕ red (floats up, red edge, → `.log.err`) · update ▲ amber
  · not-installed ghost dashed ("run setup.ps1 -Tts"). Calm by default, loud on trouble,
  motion reserved for transitions; respects "reduce motion".
- **Palette** inherited from `control.ps1` verbatim: bg `#1e1e1e`, surface
  `#252526`/`#2d2d30`, border `#3f3f46`, text `#d4d4d4`/dim `#858585`, accent `#0e639c`,
  good/LLM `#4ec9b0`, warn/MEDIA/update `#d7ba7d`→`#cc8400`, danger `#f14c4c`/`#a1260d`,
  info `#9cdcfe`. Mono (Cascadia/Consolas) for data; Segoe UI Variable for chrome.

## File / build plan

Root: **`D:\Projects\DokiCode\control\`** (new). Launched via `control.bat` (regenerated to
run the built exe / `dotnet run` in dev) and via **`doki.ps1 panel`**.

- **Phase 1 — skeleton + status board (read-only MVP):** `DokiDex.Control.csproj`,
  `App.xaml(.cs)`, `Models/` (the records), `Services/DokiService.cs` +
  `RegistryService.cs`, `ViewModels/MainViewModel.cs` + `ServiceViewModel.cs` +
  `GpuViewModel.cs`, `Views/MainWindow.xaml(.cs)` + `DashboardView`, `Themes/Palette.xaml`.
  *Verify:* window opens; cards render grouped LLM/MEDIA from the live registry; dots
  reflect real health; killing a service flips its dot within 2 s.
- **Phase 2 — control + mode switcher + confirm + GPU meter:** `GpuMeter`,
  `ModeSwitcher` (segmented radio + hover-preview + headroom bar), `ConfirmSheet`;
  `[RelayCommand]` Start/Stop/Restart/SwitchMode/StopAll → `DokiService`.
  *Verify:* GPU meter live; mode buttons run the right `doki up`; media switch shows the
  confirm sheet; per-card start/stop work.
- **Phase 3 — live logs + per-modality test-gen:** `LogTailService`, `TestGenService`,
  `LogsViewModel`, `LogsView` (tabs + filter + pause + stderr), result tray.
  *Verify:* logs tail `.run\*.log` live with tabs/filter/pause; ⚡ test returns a real
  reply/thumbnail/audio inline.
- **Phase 4 — polish:** state-transition animations + "reduce motion", update badges
  (ported job), publish profile (`dotnet publish -r win-x64 --self-contained false`).

Each phase compiles and runs on its own; Phase 1 is a usable read-only board, Phase 2 a
usable controller.

## Scope

- **v1 (ship):** grouped data-driven dashboard; mode switcher with hover-preview + 32 GB
  headroom + eviction confirm; aggregate GPU meter; per-service start/stop/restart; live
  file-tailed logs; per-modality ⚡ test; the full state vocabulary; `doki panel` launch.
- **v2 (nice-to-have):** llama-swap `/api/events` SSE push; in-app model-swap dropdown;
  Verify PASS/FAIL grid (needs `verify --json`); one-click Update actions; SwarmUI media
  detail drawer; WPF-UI Mica chrome.
- **Out of scope:** the ASP.NET + Svelte web variant (v2 only if LAN reach is needed);
  per-process VRAM bars (driver returns `[N/A]` — design around it).
