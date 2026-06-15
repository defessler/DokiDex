# 1. The Big Idea

← [Back to Home](Home.md) · Next: [The Moving Parts](2-the-moving-parts.md) →

---

## What's a "coding robot," anyway?

You've probably heard of AI tools that help write code — GitHub Copilot, ChatGPT, Claude Code, Codex. The newest ones aren't just chatbots. They're **agents**: you give them a job in plain English, and they go *do* it. They open your files, make changes, run your tests, see if the tests pass, and fix things until the job is done — looping on their own, like a real assistant working at your desk.

DokiDex is one of these agents. The twist is **where it runs**.

## The fork in the road: rent a brain, or own one

When you use Claude Code or Codex, your code gets sent over the internet to a giant data center. A massive AI model there does the thinking and sends answers back. You're **renting a brain** — an incredibly smart one — by the month or by the message.

That's wonderful, but it comes with strings:

- 🔒 **Your code leaves your computer.** For a hobby project, who cares. For secret company code, medical data, or anything under strict rules, that can be a deal‑breaker.
- 💳 **You pay forever.** Subscriptions and per‑use fees, every month, as long as you use it.
- 📶 **You need internet,** and you're at the mercy of their servers, rate limits, and whatever changes they make to the model next week.

DokiDex takes the other road: **own the brain.** Buy a powerful graphics card once, download some free AI models, and run the whole thing on your own desk.

```
   THE CLOUD WAY                        THE DOKIDEX (LOCAL) WAY

   your code ──🌐──▶ big data center    your code ──▶ your own PC ──▶ done
                     (rented brain)                   (your brain)
                          │                                │
                     $ every month                   $ once, up front
                     code leaves home                code never leaves
                     needs internet                  works offline
```

## So why doesn't *everyone* do this?

Because owning the brain has a catch: **a computer that fits under your desk is much smaller than a data center.**

The cloud's brain can be enormous. The brain you can fit on one graphics card is far smaller — like comparing a research library to a really good personal bookshelf. The personal bookshelf is still genuinely useful! But it doesn't hold everything.

In AI terms, "brain size" is roughly the **number of parameters** a model has (think of parameters as the little knobs the model learned to set during training — more knobs, more nuance). The cloud models are gigantic. DokiDex's everyday model has about **30 billion** of them, which sounds like a lot — and is — but the frontier cloud models are far larger still.

This leads to the single most important expectation to set:

> **DokiDex is excellent at the everyday stuff and noticeably weaker on the really hard, sprawling problems.** That's not a bug to be fixed — it's the physics of fitting a brain on one desk.

[Page 4](4-local-vs-the-big-clouds.md) puts real numbers on exactly where it keeps up and where it falls behind.

## The clever part: closing the gap without a bigger brain

Here's the insight the whole project is built on. The people who make Claude Code have said, roughly, that the magic is **~80% the brain (the model) and ~20% the harness** — the harness being all the *plumbing* around the brain: the part that reads files, runs commands, manages permissions, and keeps the assistant organized.

That 20%? **Anyone can build it with free, open‑source tools.** It's not secret sauce.

So DokiDex's strategy is:

1. **Copy the harness 1‑for‑1** using the best open‑source pieces (you'll meet them on page 2). This part can be just as good as the cloud's.
2. **Accept a smaller brain** — but pick a really clever one, and squeeze every drop of quality out of it with good habits and good plumbing.
3. **Swap in a better brain whenever one comes out.** New free AI models drop almost monthly. When a better one fits on the card, you download it and you're instantly smarter — for free. Upgrading the brain is a *config change*, not a rebuild.

That last point is the quiet superpower. The cloud's brain improves when *they* decide. DokiDex's brain improves whenever the open‑source world ships something better — and you get to keep all the privacy and zero‑cost benefits the whole time.

## The one exception to "fully local"

DokiDex runs **no AI in the cloud**. But it's allowed to do one internet‑y thing: **plain web search**, so it can look up current information (like "what's the latest version of Node.js?"). 

Searching DuckDuckGo isn't "sending your code to an AI company" — it's the same as you opening a search box. Your code stays home; only your search words go out, and only when the robot decides it needs to look something up. More on this on [page 2](2-the-moving-parts.md).

---

← [Home](Home.md) · Next: [The Moving Parts](2-the-moving-parts.md) →
