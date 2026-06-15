# 7. Quick Start — How Do I Actually Use It?

← [Glossary](6-glossary.md) · [Home](Home.md)

---

Good news: once it's installed, **daily use is basically two commands** — turn on the brain, then talk to the agent. Everything else on this page is optional polish.

> 🧰 **This is the *driving* guide, not the *building* guide.** It assumes the one‑time setup is done — models downloaded, Crush installed, configs in place. If you're starting from a bare machine, the full build steps are in the [design doc](../TDD.md) (§6–7). There's a setup checklist at the [bottom of this page](#one-time-setup-checklist-if-not-already-done).
>
> All commands are run from the **DokiDex folder** in PowerShell.

> 🖥️ **Prefer a GUI?** First run `.\control.bat` once (it builds the panel + creates a console‑free **DokiDex.lnk** launcher); after that just double‑click **DokiDex.lnk** for the cinematic boot + the **DokiDex** control panel — live status, GPU trust‑meter, one‑click mode switching (**DokiCode** = chat+code / **DokiGen** = image+video), logs, per‑service ⚡tests, and in‑app auto‑update. Or run `.\doki.ps1 panel`.

## The 30‑second version

```powershell
# 1. Turn on the brain (the inference server). Run once; leave it running.
.\doki.ps1 up

# 2. Go to your project and talk to the agent.
cd C:\path\to\your\project
crush
```

Now just type what you want in plain English — *"fix the failing test in cart.js"* — and watch it work. ([Page 3](3-a-task-step-by-step.md) shows exactly what happens next.)

---

## Step 1 — Turn on the brain 🧠

```powershell
.\doki.ps1 up            # default 'agent' profile: llama-swap (:8080) + speech (TTS :8004, STT :8005)
```

This is the **control plane**: it starts **llama‑swap** (the "receptionist" from [page 2](2-the-moving-parts.md)) on **http://127.0.0.1:8080** plus the speech servers, tracks their PIDs/logs, and enforces the GPU's one‑group‑at‑a‑time rule. For editor autocomplete too, use `.\doki.ps1 up coexist`. (The low‑level path `.\serving\start-serving.ps1 -Detach` starts just llama‑swap, outside doki's tracking.)

- You **don't** pick a model here. The brains load **on demand** — the first time something asks for `coder-fast`, llama‑swap loads it; ask for `coder-big` later and it swaps. So the *first* message to a brain is slow while it loads (coder‑fast ≈ 7 seconds, coder‑big ≈ a minute to load 60 GB), then it's fast.

**Check it's alive** (optional):

```powershell
.\doki.ps1 status                                  # every service: installed / running / healthy + GPU
.\serving\test-toolcall.ps1 -Model coder-fast      # a real tool-call: expect TOOLCALL OK + a speed readout
```

…or open the dashboard in a browser: **http://127.0.0.1:8080/ui**

---

## Step 2 — Talk to the agent ✋

```powershell
cd C:\path\to\your\project    # ⚠️ a project saved in git — see Golden Rules
crush
```

Crush opens its chat. Type a task with a **clear finish line** and let it loop (read → edit → test → report). When it wants to edit a file or run a command, it **asks permission** — glance at it and approve.

**Prefer a one‑shot, no‑chat run?** (great for scripts or quick jobs):

```powershell
crush run -q -m local/coder-fast -- "make the failing test in cart.js pass"
```

> The `--` matters: it tells Crush "everything after this is my prompt, not a command flag."

---

## Which brain should I pick? 🧠⚡

Crush starts on **coder‑fast** (the default). To use another, switch models inside Crush, or pass `-m local/<name>` on a one‑shot run.

| Brain | When to use it | Speed |
|-------|----------------|-------|
| **coder‑fast** | Default. ~95% of work — fixes, tests, explanations. | ⚡⚡⚡ fast (~265 tok/s) |
| **coder‑big** | Opt in for genuinely hard reasoning (tricky logic, architecture). | 🐢 slow (~27 tok/s) |
| **coder‑fast‑lite** | Same as coder‑fast but slimmer — use it when editor **autocomplete is running too** (so both fit on the card). | ⚡⚡⚡ fast |

---

## Step 3 (optional) — Editor autocomplete ✍️

Want Copilot‑style "finish my line" suggestions in VS Code, locally?

```powershell
.\doki.ps1 up coexist     # the agent + the tiny FIM autocomplete brain on :8012 (low-level: serving\start-fim.ps1)
```

One‑time setup: install the **llama.vscode** extension and copy `harness\llama.vscode-settings.json` into your VS Code user settings (`%APPDATA%\Code\User\settings.json`). It points the extension at `:8012` and turns Copilot off.

> ⚠️ **Coexistence caveat:** autocomplete and the agent share the graphics card. When you run both at once, use **coder‑fast‑lite** for the agent — the full‑size coder‑fast won't fit alongside the FIM brain. (Together they sit at ~27.6 GB of 32 GB.)

---

## Step 4 (recommended) — Prove you didn't break anything ✅

Changed a model, a setting, or the harness? Run the **driving test** ([page 5](5-why-its-built-this-way.md)) and compare the score to last time. The motto: *never tune blind.*

```powershell
# Full 14-task suite -> writes a scorecard into docs\scorecards\
.\evals\run-suite.ps1 -Harness crush -Model coder-fast

# Or just one task while you're iterating:
.\evals\run-eval.ps1 -Harness crush -Model coder-fast -Task t2-slugify-bug
```

---

## The golden rules (don't skip these) 📏

These are what make a *local* brain reliable — see [page 3](3-a-task-step-by-step.md) for the why.

- 🔒 **Only point it at a git‑committed project.** Headless `run` executes commands *without asking* — git is your undo button.
- 🎯 **Give it a checkable finish line.** "Make the tests pass," not "improve this."
- ✂️ **Small tasks.** One bug or feature at a time. For big jobs, ask for a numbered **plan first**.
- 🆕 **One task per chat.** Start fresh each time.
- 🏠 **Leave an `AGENTS.md`** in each repo (test command, code style, house rules). Biggest reliability win there is.

---

## Stopping it 🛑

```powershell
.\doki.ps1 down      # stops every managed service (llama-swap, FIM, TTS, STT, media) and frees the GPU
```

(`doki down` is the clean stop — it tears down everything `doki up` started, including the TTS/STT servers, so nothing is left holding a GPU slot. A raw foreground server stops with Ctrl+C in its own window.)

---

## Troubleshooting quick hits 🔧

| Symptom | Fix |
|---------|-----|
| Agent says nothing / can't connect | Run `.\doki.ps1 status` (or check **http://127.0.0.1:8080/ui**). |
| First message to coder‑big hangs ~a minute | That's the 60 GB model loading. Later turns are fast. |
| Tool calls misfire / flaky edits | Lower the **temperature** in the harness model options *before* blaming the brain. |
| "Out of memory" on the GPU | Use a smaller context (coder‑fast‑lite) or step down a quant — see [design doc](../TDD.md) §6.1. |
| Autocomplete + agent crash with OOM | Use **coder‑fast‑lite**, not coder‑fast, while editing live. |

---

## One‑time setup checklist (if not already done)

You only do this once per machine. Full details in the [design doc](../TDD.md) §6–7.

- [ ] **Models** downloaded into `models\` (git‑ignored): the ~30B coder, the ~120B coder, and `qwen2.5-coder-3b-q8_0.gguf` for FIM
- [ ] **llama.cpp + llama‑swap** binaries in `serving\` (CUDA build)
- [ ] **Crush** installed (`winget install charmbracelet.crush`)
- [ ] **Crush config:** copy `harness\crush.json` → `%USERPROFILE%\.config\crush\crush.json`
- [ ] *(autocomplete)* **llama.vscode** extension + `harness\llama.vscode-settings.json` → VS Code user settings
- [ ] *(web search)* **uv** installed, so `uvx duckduckgo-mcp-server` works

---

← [Glossary](6-glossary.md) · [Home](Home.md)
