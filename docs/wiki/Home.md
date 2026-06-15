# The DokiDex Wiki 🤖

*A friendly, no-jargon explainer for how this whole thing works.*

---

## DokiDex in one sentence

**DokiDex is a robot coding assistant that lives entirely inside one personal computer — it reads your code, writes code, runs commands, and looks things up on the web, all without ever sending your code to a company's cloud.**

It's built to do the same *kind* of job as the famous cloud coding assistants — **Claude Code** (running Anthropic's Opus 4.8) and **Codex** (running OpenAI's GPT‑5.5) — but on your own machine, on your own electricity, with your own rules.

---

## Start here 👇

These pages are meant to be read in order, like chapters. Each one is short.

| # | Page | What it covers |
|---|------|----------------|
| 1 | [The Big Idea](1-the-big-idea.md) | *Why* build a coding robot that lives on your own computer instead of renting one from the cloud. |
| 2 | [The Moving Parts](2-the-moving-parts.md) | The handful of pieces that make it work — the brain, the engine, the hands, the library card — each with a simple picture. |
| 3 | [Watch It Solve a Task](3-a-task-step-by-step.md) | A play-by-play of what actually happens when you say "fix this bug." |
| 4 | [Local vs. the Big Clouds](4-local-vs-the-big-clouds.md) | The honest comparison: where DokiDex wins, where Claude Code and Codex win, and why. |
| 5 | [Why It's Built This Way](5-why-its-built-this-way.md) | The interesting choices — two brains, one search engine, a "driving test" for the robot. |
| 6 | [Glossary](6-glossary.md) | Every nerdy word (VRAM, MoE, quant, MCP, FIM…) explained like you're five. |
| 7 | [Quick Start — How Do I Use It?](7-quick-start.md) | The actual commands: turn on the brain, talk to the agent, autocomplete, and the eval suite. |
| 8 | [Image & Video Generation](8-image-and-video.md) | Making local, unfiltered pictures and short videos on the same GPU. |

> 🚀 **Just want to run it, not read about it?** Skip straight to the [Quick Start](7-quick-start.md).

---

## The 30‑second version

Imagine hiring a junior programmer who:

- 🧠 **Lives in your computer.** Never phones home. Your code never leaves the building.
- 💸 **Works for free** once you've bought the computer. No subscription, no per‑message charge, no usage limits.
- ✈️ **Works offline.** On a plane, in a bunker, no internet required (except when *you* ask it to search the web).
- ⚡ **Is fast at the everyday stuff** — small fixes, writing tests, explaining code.
- 🙋 **Needs clearer instructions** than the superstar cloud assistants, and gets stuck on the really gnarly, sprawling problems.

That trade — *a bit less brilliant, but private, free, and entirely yours* — is the whole point of DokiDex.

---

## Who built it and on what

One person, on one gaming PC:

- **Graphics card:** RTX 5090 with **32 GB of video memory** (this is the "desk space" the robot's brain has to fit on — page 2 explains why that matters)
- **64 GB of regular RAM**, a fast Intel CPU, **Windows 11**
- Everything in the stack is **free and open‑source**.

Everything below is the story of how those parts add up to a working coding robot.

> 📎 This wiki is the *friendly* version. The precise engineering details live in [`docs/TDD.md`](../TDD.md) (the design doc), [`docs/benchmarks.md`](../benchmarks.md) (the measurements), and [`docs/decisions.md`](../decisions.md) (the "why we picked X" log).
