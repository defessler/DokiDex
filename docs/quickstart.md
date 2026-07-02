# DokiGen Studio — Quick Start

The 5-minute path to making something in **the app** (the DokiGen Studio web studio). This is the *app* quickstart; for the **coding agent** quickstart (turn on the brain → `crush`) see [`wiki/7-quick-start.md`](wiki/7-quick-start.md), and for the full feature walkthrough see [`tutorial.md`](tutorial.md).

> **Prerequisite:** DokiDex is installed (NVIDIA GPU + the stack). Not installed yet? See [Install & first run](tutorial.md#2-install--first-run).

---

## 0 · Launch the app

Double-click **`DokiDex.lnk`** (or run `.\doki.ps1 panel`). The control panel boots, then opens — the **DokiGen Studio** is one of its pages. Prefer a browser? The studio also lives at **http://127.0.0.1:5111**.

The studio UI loads on its own, but its features need a **GPU mode** running (next step).

## 1 · Understand the one rule: GPU modes

There's a single 32 GB GPU, and it can't hold the chat brain *and* the image/video engine at once. So DokiDex runs in **one mode at a time**:

| Mode | Use it for | What runs |
|---|---|---|
| **agent** | Chat, voice (TTS/STT), Director, Cast | the LLM `:8080` + speech |
| **media** | Image, video, music, edit, foley | SwarmUI `:7801` |
| **coexist** | Coding + live editor autocomplete | the LLM `:8080` + FIM `:8012` |

Switch modes from the panel's **mode switcher** (or the studio's **Status** view, or `.\doki.ps1 up media`). When you hit **Generate** while in the wrong mode, the studio offers to flip for you.

## 2 · Your first image 🖼️

1. Open **Create** (the default studio view).
2. Leave the kind on **image**.
3. Type a prompt: *"a neon koi dragon coiled around a lantern, rain, cinematic"*.
4. Click **Generate**. If you're in agent mode, approve the switch to **media**.
5. Watch the live preview fill in. The finished image drops into the **Library**.

## 3 · Your first chat 💬

1. Make sure you're in **agent** mode (panel mode switcher).
2. Open **Chat**, type a message, press **Ctrl+Enter** (or **Send**).
3. Pick **fast** (snappy) or **quality · slower** (the heavy 120B brain) from the Speed dropdown.
4. Want it to *see* an image? Click **+ image**, pick one from your Library, then ask about it.

## 4 · Your first video 🎬

1. In **Create**, pick the **video** kind.
2. Prompt the motion: *"a drone glides over a misty canyon at sunrise"*.
3. (Optional) pick a **Camera** preset like *dolly-in*.
4. **Generate** (media mode). The MP4 lands in the **Library**.

## 5 · Your first voice 🔊

1. **agent** mode. Open **Voice**.
2. Type a line, pick a voice (or *default*), click **Speak**. The clip plays and saves to the Library.

---

## Where things go

Everything you generate — images, video, music, speech — lands in the **Library** view: search it, favorite/trash with the **F**/**X** keys, and **remix** any card back into Create.

## 6 · Code with doki code

`doki code` is a terminal coding agent (mirrors Claude Code) running Qwen3-Coder-30B locally — no cloud. Use it from any project directory.

1. Make sure you're in **agent** mode (`.\doki.ps1 up agent`).
2. `cd` into the project you want to work on.
3. Run `.\doki.ps1 code` (interactive REPL) or `.\doki.ps1 code "<task>"` (one-shot, then exit).
4. The agent proposes file edits and shell commands; each one shows a colored diff or the command text and waits for **[y]es / [a]lways / [n]o** (default: **no**).
5. Review changes with `git diff` at any time; `/undo` reverts the last change in-session.

Type `/help` for slash commands (model-swap, clear, cwd, undo, exit). Ctrl+C interrupts the current turn.

---

## Next steps

- The full walkthrough of every view and control → **[tutorial.md](tutorial.md)**
- Full `doki code` walkthrough → [tutorial.md — §14](tutorial.md#doki-code--local-coding-agent)
- The Crush coding CLI (harness-based) → [wiki/7-quick-start.md](wiki/7-quick-start.md)
- Exact API call for every capability → [wiki/11-media-recipes.md](wiki/11-media-recipes.md)
