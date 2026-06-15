# 3. Watch It Solve a Task

← [The Moving Parts](2-the-moving-parts.md) · [Home](Home.md) · Next: [Local vs. the Big Clouds](4-local-vs-the-big-clouds.md) →

---

Theory's fine, but let's watch the robot actually work. We'll fix a small bug, in slow motion, so you can see how the brain and the hands pass the job back and forth.

## The setup

Say you have a function that turns a title into a web address — "My First Post" should become `my-first-post`. But it's buggy: it's leaving the spaces in, so you get `my first post`. You open DokiDex in your terminal and type:

> *"The slugify function is leaving spaces in instead of turning them into dashes. Find it, fix it, and make the tests pass."*

Here's what happens next. Watch how it's a **loop** — think, act, look at the result, repeat.

## The loop, step by step

**1. 🧠 The brain reads your request and makes a plan.**
It can't see your files yet, so its first move is to ask for them. It fills out a "request form" (a **tool call**): *"Search the project for a function named `slugify`."*

**2. ✋ The hands carry it out.**
Crush (the hands) runs the search across your files and hands the brain back the result: *"Found it — in `text_utils` , line 42."* The brain still hasn't touched anything; it just looked.

**3. 🧠 The brain asks to read the actual code.**
Another tool call: *"Show me that file."* The hands open it and feed the brain the contents.

**4. 🧠 The brain spots the bug and proposes a fix.**
It sees the line that's supposed to swap spaces for dashes and notices it's wrong. It makes an **edit** tool call: *"Change line 43 from this to that."*

**5. 🛑 The hands pause and ask you.**
Because this changes a file, Crush stops and asks: *"OK to make this edit?"* You glance at it — looks right — and say yes. (This is the **permission** system. You can also tell it "stop asking, I trust you for this session.")

**6. ✋ The hands make the change.**
Crush edits the file exactly as the brain specified.

**7. 🧠 The brain wants proof it worked.**
It doesn't just *assume* the fix is good — it asks to run the tests. Tool call: *"Run `dotnet test`."* (It knows that's the test command because your **house‑rules note**, [`AGENTS.md`](2-the-moving-parts.md#-the-house-rules--agentsmd), told it.)

**8. ✋ The hands run the tests and report back.**
Crush runs them and hands the brain the output: *"All tests passed. ✅"*

**9. 🧠 The brain declares victory.**
It tells you, in plain English: *"Fixed it — `slugify` now replaces spaces with dashes, and all tests pass."* Done.

```
   YOU ──▶ 🧠 think ──▶ ✋ act ──▶ 👀 look at result ──┐
              ▲                                        │
              └──────────── repeat until done ◀────────┘
```

That find → read → edit → test → report loop, with the brain thinking and the hands acting between each step, is **the entire game**. A big task is just this loop running more times.

## A couple of things happening behind the curtain

**"Why didn't it get slower as the conversation got longer?"**
Every time the brain takes a turn, it technically has to re‑read the *whole* conversation so far (that's just how these models work). That could get painfully slow. DokiDex uses a trick called a **prompt cache** — the engine remembers the earlier part of the conversation instead of re‑chewing it every time, so each turn stays quick. Think of it as the brain keeping its place with a bookmark instead of re‑reading the book from page one each turn.

**"What if the brain asks to do something dangerous?"**
The permission prompt in step 5 is your safety net — DokiDex asks before edits and risky commands. There's also a blunt rule the project lives by: **always work inside a project that's saved in Git** (a system that snapshots your code). If the robot ever does something dumb, Git is the undo button. This matters extra in *fully‑automatic mode* (used for testing), where the robot is allowed to act without asking — there, Git is the only safety net, so it's only ever pointed at throwaway copies.

**"How does it know my test command, my code style, my quirks?"**
From the `AGENTS.md` house‑rules note. The more clearly that note is written, the smoother this whole loop runs. When the robot repeatedly gets something wrong, the fix is usually to add one line to that note — not to scold the robot.

## The habits that make it go smoothly

Because DokiDex's brain is smaller than a cloud brain, *how you ask* matters more. The project's golden rules:

- 🎯 **Give it a finish line it can check.** "Make the tests pass" or "the output should be exactly X" works far better than "improve this." A smaller brain is *much* more reliable when it can tell for itself whether it's done.
- ✂️ **Ask for small changes.** One bug, one feature at a time. Big "rewrite everything" requests are where smaller brains fall apart.
- 🗺️ **For big jobs, ask for a plan first.** "Read the code and write a numbered plan, don't edit yet." Review the plan, then say "do steps 1–2." Smaller brains follow a good plan far better than they invent one.
- 🆕 **One task per chat.** Start fresh for each new job. Long, rambling sessions get muddled.

Follow those and DokiDex is a genuinely strong helper. Ignore them and it gets confused — exactly the kind of hand‑holding a cloud brain needs less of, which is the perfect segue to the comparison. →

---

← [The Moving Parts](2-the-moving-parts.md) · [Home](Home.md) · Next: [Local vs. the Big Clouds](4-local-vs-the-big-clouds.md) →
