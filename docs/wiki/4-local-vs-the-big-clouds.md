# 4. Local vs. the Big Clouds

← [Watch It Solve a Task](3-a-task-step-by-step.md) · [Home](Home.md) · Next: [Why It's Built This Way](5-why-its-built-this-way.md) →

---

This is the honest comparison: **DokiDex** (running on one PC) versus the frontier cloud assistants — **Claude Code** (Anthropic's Opus 4.8) and **Codex** (OpenAI's GPT‑5.5). No cheerleading. Where DokiDex wins, it wins big. Where it loses, it loses clearly.

## The one‑breath summary

- For **everyday coding** — small fixes, tests, explanations, autocomplete — DokiDex is *close enough* that you often won't feel the difference.
- For the **really hard stuff** — huge, vague, sprawling problems that touch many files and need deep reasoning — the cloud assistants are clearly, meaningfully better.
- DokiDex trades a slice of that top‑end brilliance for three things the cloud can't give you: **privacy, zero ongoing cost, and total control.**

## Where DokiDex wins 🟢

**🔒 Privacy — your code never leaves the building.**
This is the headline. Claude Code and Codex send your code to their servers to think about it. DokiDex doesn't — at all. For secret company code, regulated data, or anything you simply don't want on someone else's computer, this isn't a nice‑to‑have, it's the whole reason to do this.

**💸 Free to run, forever.**
After the hardware is paid for, there's no subscription and no per‑message meter. You can let it churn all day and the only cost is electricity. The cloud tools bill you every month, sometimes per use, and can get expensive fast if you lean on them.

**♾️ No limits, no throttling, no surprises.**
No "you've hit your usage cap," no rate limits, no waiting in line at busy times, and no morning where the model quietly changed and your workflow feels different. It does exactly what it did yesterday.

**✈️ Works offline.**
On a plane, in a secure facility, during an internet outage — it just works. (The optional web search is the only thing that needs a connection, and only when you ask for it.)

**🔧 Totally yours to tinker with.**
Every layer is open and swappable. Don't like the brain? Download a better one. Want different hands? Swap the harness. No vendor can lock you in, deprecate your tool, or change the terms.

**⚡ Genuinely fast on routine work.**
The everyday brain cranks out ~265 words of code per second, and autocomplete is **basically as good as the cloud's** — small "finish my line" models are a solved problem. For the bread‑and‑butter loop, it feels snappy.

## Where the big clouds win 🔵

**🧠 Raw brainpower on hard problems.**
This is the real gap. The cloud models are vastly larger than anything that fits on one graphics card. On big, ambiguous, multi‑file tasks — the kind where you describe a fuzzy goal and expect the agent to figure out a sprawling change across a codebase — Claude Code and Codex are simply smarter and more likely to nail it in one shot.

**🙌 Less hand‑holding.**
The cloud brains are more self‑correcting. They recover from their own mistakes, infer what you meant, and stay coherent over long, messy sessions. DokiDex needs you to follow the good habits from [page 3](3-a-task-step-by-step.md) — clear finish lines, small steps, plan‑first. Skip those and it struggles where a cloud brain would've coasted.

**🎁 Turnkey — zero setup, zero upkeep.**
Claude Code and Codex: install, log in, go. DokiDex: you buy a serious graphics card, download multi‑gigabyte models, tune settings, manage what fits in memory, and keep it all updated. **You are the IT department.** That's part of the fun for a tinkerer and a chore for everyone else.

**💰 Cheap to start.**
The cloud tools need only a laptop and a subscription. DokiDex needs a powerful, pricey GPU up front. You're trading a big one‑time hardware cost for low running costs — which only pays off if you use it a lot.

## The honest scorecard

How close is "close"? The project's own design doc sets these expectations, and its [eval suite](5-why-its-built-this-way.md) measures them:

| The job | DokiDex (local) vs. frontier cloud |
|---|---|
| **Autocomplete** (finish my line) | 🟢 **Basically a tie.** Small local models are great at this. |
| **Routine, well‑scoped tasks** (a bug, a test, a small feature) | 🟢 **~80–90% as good.** You often won't notice the difference. |
| **Following instructions / tool use** | 🟢 **Within a few %.** Very reliable when set up right. |
| **Hard, long, vague, multi‑file tasks** | 🔵 **Clearly behind** — the cloud pulls meaningfully ahead here. |
| **Privacy** | 🟢 **No contest** — code never leaves your machine. |
| **Running cost** | 🟢 **~$0** after hardware vs. a forever subscription. |
| **Up‑front cost & setup** | 🔵 **The cloud is far easier** — laptop + login vs. a pricey GPU + tinkering. |

> 📊 On DokiDex's own 11‑task test (real little coding jobs), the everyday brain passed **10 out of 11 (91%)**. The one it failed was a tricky refactor where a hidden test caught a subtle behavior change — exactly the "deeper reasoning" category where smaller brains are weakest. That single failure is the whole story of the gap in miniature.

## Why the gap is smaller than you'd think — and shrinking

Two things keep this from being a lopsided fight:

**1. The plumbing is identical.** Remember the "~80% brain, ~20% harness" idea from [page 1](1-the-big-idea.md)? DokiDex's hands (the harness) are built from the same open‑source parts the pros use — that 20% is essentially matched. The *entire* remaining gap is brain size. DokiDex isn't a worse *tool*; it's the same tool with a smaller brain.

**2. The brain gets upgraded for free.** New open‑source AI models come out almost monthly, each a little smarter than the last. When a better one fits on the card, DokiDex swaps it in — a config change plus a quick re‑run of the test suite — and instantly closes part of the gap, at no cost. The cloud's brain improves on *their* schedule; DokiDex's improves on the *whole open‑source world's* schedule.

## The real conclusion: it's not either/or

Here's the grown‑up take: **most people who run something like DokiDex also keep a cloud tool around.** They're not rivals so much as a toolbox.

- Reach for **DokiDex** for the daily grind, anything private, anything offline, and to never think about a bill.
- Reach for **Claude Code or Codex** when you hit a genuinely hard, sprawling problem and want the biggest brain money can rent.

DokiDex's goal was never "beat the cloud." It was "get *most* of the way there, on my own hardware, on my own terms, for free" — and that's a goal it hits.

---

← [Watch It Solve a Task](3-a-task-step-by-step.md) · [Home](Home.md) · Next: [Why It's Built This Way](5-why-its-built-this-way.md) →
