# 5. Why It's Built This Way

← [Local vs. the Big Clouds](4-local-vs-the-big-clouds.md) · [Home](Home.md) · Next: [Glossary](6-glossary.md) →

---

DokiDex made a bunch of deliberate choices, and the reasons are genuinely interesting — and very ELI5‑able. Here are the big ones.

## Why two brains instead of one big one

You might think: just always use the biggest, smartest brain. But the big brain is **~10× slower** (27 vs. 265 words/sec — page 2). Using it for "rename this variable" is like taking a freight train to the corner shop.

So DokiDex keeps a **fast brain for the 95% of work that's routine** and a **big brain you summon on purpose** for the genuinely hard 5%. Right tool, right job. Same reason you own both a microwave and an oven.

## Why "Crush" got the job — a contest, not a hunch

There were two good candidates for the "hands" (the harness): **Crush** and **OpenCode**. Instead of picking by gut feeling, DokiDex held a **bake‑off** — a head‑to‑head contest. It ran the *same* real coding tasks through both, with both brains, and measured: Did it finish? Was the edit correct? How long did it take?

The result: **both were good**, but Crush won the daily‑driver job because it was more *reliable* (zero hiccups in the contest), it's built to run cleanly on Windows, and its permission controls fit the way DokiDex tests itself. OpenCode was actually a touch *faster* and has some genuinely nice tricks, so it's kept on the bench as a challenger — ready to be re‑judged any time.

> The lesson DokiDex keeps repeating: **decide with data, not vibes.** Which leads to the next choice…

## Why there's a "driving test" for the robot

How do you know a change made DokiDex *better* and not secretly *worse*? You can't tell from a couple of lucky runs. So the project built a **golden‑task eval suite** — basically a **driving test** for the robot.

It's a set of **11 real little coding jobs**, each with an answer key (an automatic check for "did it actually work?"). Any time *anything* changes — a new brain, a new setting, a new harness — you re‑run the whole test and compare the score to the last one. Today's score is **10/11 (91%)**.

This is the quiet hero of the whole project. It means every future tweak is **measured, not guessed**. The motto written into the docs is *"never tune blind."* A bonus: the test also catches the robot trying to *cheat* (e.g., editing the test instead of fixing the code) — there are hidden checks it can't see.

## Why the toolbox is kept almost empty

You'd think *more* tools = *more* capable robot. For a smaller brain, the opposite is true. Every extra tool you hand it is one more thing it can pick *wrong*. Give a small brain twenty tools and it starts fumbling which to use.

So DokiDex is ruthless: it plugs in **exactly one** outside tool — web search — and nothing else. A sharp, near‑empty toolbox beats a cluttered one. (This is also why the house‑rules note, `AGENTS.md`, is kept short — every extra line is clutter the brain has to wade through on every turn.)

## Why autocomplete uses the *smallest* brain

For the "finish my line as I type" feature, DokiDex picked a *tiny* 3‑billion‑knob brain — even though a bigger one was available. Two reasons:

1. **Autocomplete must be instant.** A small brain answers in a blink; a big one would lag, and a laggy autocomplete is worse than none.
2. **It has to share the desk.** Autocomplete runs *at the same time* as the main coding brain, so both must fit in 32 GB of card memory together. The tiny model leaves room; a bigger one wouldn't. (To make the math work, the main brain runs in a slightly slimmed‑down "lite" mode while you're editing live — together they use ~27.6 of the 32 GB.)

Smaller, faster, and it fits. For this job, less is more.

## Why everything lives in Git

There's a blunt safety rule: **only ever point the robot at code that's saved in Git.** Git is a system that snapshots your code so you can rewind to any past version.

Why so strict? Because in its fully‑automatic test mode, the robot can run commands *without asking* — and it was measured actually deleting a file when told to. That's fine **if** you can undo it. Git is that undo button. So the rule is absolute: committed Git state first, robot second. No exceptions.

## Why it's designed to be *replaced* piece by piece

The cleverest design choice is that DokiDex is built to **get better without being rebuilt.** Every part is swappable:

- A smarter free brain comes out next month? Download it, run the driving test, swap it in if it scores higher. **Done — instantly smarter, still free, still private.**
- A better set of hands appears? The eval suite can re‑judge it the same day.
- More RAM added to the PC? Even bigger brains become possible.

There's even a "someday" idea in the design doc: once enough is learned from using Crush and OpenCode, build a *custom* harness — "DokiCode proper" — with this same eval suite as its report card. The whole thing is a platform that rises as the open‑source world rises, for free.

> **The throughline of every choice above:** measure everything, keep it lean, stay swappable, and never trust a claim you haven't tested on your own machine.

---

← [Local vs. the Big Clouds](4-local-vs-the-big-clouds.md) · [Home](Home.md) · Next: [Glossary](6-glossary.md) →
