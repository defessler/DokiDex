# DokiDex — All-in-One Installer + Manager (design)

**Date:** 2026-06-16 · **Status:** approved (build all)

## Goal

Turn the DokiDex control panel from "a manager that requires a cloned repo beside it" into a
**standalone app that is the product**: download one exe → it installs the whole stack → then manages
it. The app is built from the repo by CI but is **independent of any cloned repo at runtime**.

## Locked decisions

1. **App is the product**, built from the repo; no cloned repo required at runtime.
2. **Hybrid engine:** native C# **control plane** (status + lifecycle) + **bundled PowerShell** for
   the heavy/fiddly work (setup, model downloads, SwarmUI install, gen).
3. **One user-picked install folder** on first run; app state in `%LocalAppData%\dokidex`.
4. **Prereqs:** detect + **one-click-each** (winget for git/python/uv; guide for App Installer + GPU
   driver). The exe is self-contained .NET — dotnet runtime not required to run it.
5. **First run:** **adopt an existing DokiDex folder** OR **fresh install**.

## Architecture

**Two modes on launch**, decided by `%LocalAppData%\dokidex\settings.json` → `InstallRoot`:
- unset/invalid → **Installer (Setup Wizard)**
- set + valid → **Manager** (today's panel + native control plane)

**Native ↔ script boundary:**
- **Native C# (no pwsh):** the status poll (HTTP health, `.run\*.pid`, `nvidia-smi`, llama-swap
  `/running` + `/v1/models`) and lifecycle orchestration (GPU-group exclusion, start order,
  health-wait, stop via taskkill). A C# `ServiceRegistry` mirrors `doki.ps1`'s `$Services`/`$Profiles`,
  with a test asserting they stay in sync.
- **Bundled PowerShell (heavy):** `setup.ps1`, `download_models.py`, the per-service launchers
  `serving/start-*.ps1` (invoked by the native orchestrator), `doki-gen.ps1`, `verify.ps1`. pwsh stays
  a prereq for these, not for status.

**Layout:** `InstallRoot` holds scripts/configs/models/SwarmUI. `%LocalAppData%\dokidex` holds app
state (`InstallRoot`, `GenOutputDir`, `InstallManaged`). `InstallManaged=false` (adopted repo) is
managed **in place, never overwritten** (git owns it); `InstallManaged=true` (fresh) gets scripts
extracted from the exe and refreshed on auto-update.

**Build/release:** CI zips the runtime files into the exe as an embedded resource. The repo stays the
dev source + build input; `doki.ps1`/CLI keep working for devs.

## Phase 1 — Independent manager + adopt-existing (foundation; fixes today's bug)

App no longer walks up to find the repo; it knows its home via `InstallRoot`. Delivers immediate value
and removes the "status unavailable / must live in a repo" failure.

- **AppSettings** (`control/Services/AppSettings.cs`, exists): add `InstallRoot`, `InstallManaged`.
- **RepoPaths** → resolve `Root` from `AppSettings.InstallRoot` when set+valid; else the current
  walk-up (dev) then `ExeDir`. `DokiPs1`/`RunDir`/etc. derive from it.
- **Native control plane** (new `control/Services/Control/`):
  - `ServiceRegistry` — C# data mirroring `$Services`/`$Profiles` (name, launchScript, healthUrl,
    group, port, vramGB, requires-path, ui).
  - `StatusProbe` — builds the same `StatusDoc` the panel parses today, natively: HTTP health probes,
    pidfile reads from `<root>\.run`, `nvidia-smi` parse, llama-swap `/running` + `/v1/models`.
  - `Lifecycle` — up/down/start/stop: group mutual-exclusion + start order + health-wait, invoking the
    bundled `serving/start-*.ps1` for launches and taskkill (by pid then port) for stops.
  - `MainViewModel` polls `StatusProbe` natively (no pwsh) instead of `doki status json`.
- **First-run "locate/adopt" flow:** if `InstallRoot` unset, show a minimal locate screen (pick a
  folder containing `doki.ps1`) → validate → save `InstallRoot`, `InstallManaged=false` → Manager. The
  current "status unavailable" screen gains a "Locate DokiDex folder…" recovery button.
- **Tests:** `ServiceRegistry` matches `doki.ps1` `$Services`/`$Profiles` (parse + compare);
  `StatusProbe` parsing; `Lifecycle` decisions (group exclusion, profile order); `AppSettings`/
  `RepoPaths` resolution. Keep `doki test` green.

## Phase 2 — The all-in-one installer

- **Embedded payload:** MSBuild pre-step zips `doki.ps1, verify.ps1, setup.ps1, serving/, harness/,
  media-assets/` → `EmbeddedResource`. `Payload` service extracts to `InstallRoot` on fresh install +
  refresh-on-update (scripts only; never models/user configs).
- **Setup Wizard** (`Views/Installer*`, `ViewModels/InstallerViewModel`): Welcome+mode → Prereqs
  (detect + one-click-each via winget; guide App Installer + driver) → Components (checklist → setup
  flags: core / media (lean|full) / TTS / STT, with per-component size + free-space check) → Install
  (extract payload, then stream `setup.ps1 -Media -Models … [-Tts] [-Stt]` + `download_models.py` into
  a live log + progress) → Done (persist `InstallRoot`, `InstallManaged=true`, switch to Manager).
- **Update-refresh:** on launch after an exe update, if `InstallManaged`, re-extract payload scripts.
- **Build/CI:** `release.yml` packages the payload; single-file exe stays the artifact.
- **Tests:** component→flags mapping; prereq detection; path/space validation; payload extract/refresh
  (overwrite scripts, preserve models) on a temp dir.

## Testing strategy

Pure/native logic is unit-tested (xUnit) — registry sync, status parse, lifecycle decisions,
flag-mapping, payload extract. Live install/gen stays integration (`doki verify`). `doki test` stays
green throughout.

## Migration

The user's `D:\Projects\DokiDex` → first run "Use existing folder" → `InstallRoot=that`,
`InstallManaged=false` → managed in place, scripts never overwritten (git owns them).

## Risks

- **Registry drift** (C# vs `doki.ps1`): mitigated by the sync test.
- **Native lifecycle parity** with `doki.ps1`: port carefully; the launchers stay scripts.
- **Payload staleness** on adopted repos: never extract over `InstallManaged=false`.
- **Read-only/space**: validated in the wizard; output-dir already has a writable fallback.
