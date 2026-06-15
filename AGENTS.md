# AGENTS.md — DokiDex

## Project

A fully-local AI agentic-coding + media stack on a single RTX 5090 (32 GB), Windows 11
native (no Docker/WSL), driven by a PowerShell control plane (`doki.ps1`). Chat/code +
autocomplete + web search + TTS/STT speech + image/video/music/edit generation + a WPF
control panel — all uncensored, all on one box.

## Commands

- Control plane: `.\doki.ps1 up [agent|coexist|media]` · `down` · `status [json]` · `restart` ·
  `start|stop <service>` · `logs <svc>` · `panel` · `doctor` (env/install diagnostics)
- Full-stack test (run before declaring done): `.\doki.ps1 verify`  (cycles modes, hits every
  capability with a real API call; expects all-green). Panel unit tests: `.\doki.ps1 test`.
- Install/bootstrap: `.\setup.ps1 [-Media -Models full] [-Tts] [-Stt]`
- Control panel (C# WPF): `dotnet build control\DokiDex.Control.csproj -c Release`

## Rules

- **PowerShell, not bash** — scripts are `.ps1`; use PowerShell syntax. No Docker, no WSL.
- The GPU runs **one group at a time** (32 GB): LLM (agent/coexist) vs media — they're mutually
  exclusive. `doki up` handles the switch.
- New service? add it to `$Services`/`$Profiles` in `doki.ps1` (it's data-driven — the panel and
  `status json` pick it up automatically) + a `serving\start-*.ps1` + a guarded `verify.ps1` smoke.
- `media/`, `models/`, `tts/`, `stt/`, `control/bin|obj/`, `*.gguf|*.safetensors` are git-ignored
  (huge weights / build output). Never commit them.
- Make the smallest change that solves the task; don't refactor unrelated code. List a directory
  rather than inventing a path.

## Memory

A persistent `memory` MCP is available and survives across sessions — use it:

- **Starting a non-trivial task:** `memory_search` the relevant keywords to recall prior decisions
  and gotchas (e.g. "Wan 2.2 5B uses wan2.2_vae"; "no flash-attn wheel for Blackwell sm_120").
- **On a decision, gotcha, or non-obvious fact:** `memory_save` it (one fact per note, with
  comma-separated `tags`). Don't save what's already obvious from the code or git history.
