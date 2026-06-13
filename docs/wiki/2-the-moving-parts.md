# 2. The Moving Parts

← [The Big Idea](1-the-big-idea.md) · [Home](Home.md) · Next: [Watch It Solve a Task](3-a-task-step-by-step.md) →

---

DokiCode is made of a few pieces that each do one job. Here's the whole machine as a picture, then each part explained with a simple analogy.

```
        YOU type a request in the terminal
                     │
                     ▼
   ┌─────────────────────────────────────┐
   │  THE HANDS — "Crush" (the harness)   │  reads files, edits code,
   │  the part that DOES things           │  runs commands, asks permission
   └───────┬──────────────────┬──────────┘
           │ "think for me"   │ "search the web for me"
           ▼                  ▼
   ┌───────────────┐   ┌──────────────────┐
   │ THE ENGINE    │   │  THE LIBRARY CARD │  (DuckDuckGo web search)
   │ ROOM          │   └──────────────────┘
   │ llama.cpp +   │
   │ llama-swap    │   loads & runs the brain you asked for
   └───────┬───────┘
           ▼
   ┌───────────────────────────────────┐
   │  THE BRAINS (AI models)           │
   │  • fast everyday brain            │
   │  • big careful brain              │
   │  • tiny autocomplete brain        │
   └───────────────────────────────────┘
        all living on the RTX 5090 graphics card
```

Let's meet them one at a time.

---

## 🧠 The Brains — the AI models

The "brain" is the AI model — the thing that actually does the thinking. DokiCode keeps **three** of them, because no single brain is best at everything. Think of it like a small crew:

### The everyday brain — `coder-fast`
- **Who it is:** a model called *Qwen3‑Coder‑30B* (the "30B" means ~30 billion of those knobs from page 1).
- **The vibe:** quick, capable, your default for ~95% of work — small fixes, writing tests, explaining code.
- **How fast:** it "types" about **265 words‑worth of code per second**. That's faster than you can read. It fits *entirely* on the graphics card, which is why it's so snappy.

### The heavy‑hitter brain — `coder-big`
- **Who it is:** a much larger model (*gpt‑oss‑120b* — ~120 billion knobs).
- **The vibe:** smarter and more careful, for the genuinely hard problems (tricky logic, big architectural decisions). You opt into it on purpose.
- **The catch:** it's too big to fit entirely on the graphics card, so part of it spills over into regular RAM (slower memory). That makes it think more like **27 words per second** — roughly 10× slower than the everyday brain. Smart, but you wait for it.

> 🍰 **Why two brains?** Same reason a kitchen has both a microwave and an oven. The microwave (fast brain) handles most meals in seconds. The oven (big brain) is slower but does the hard stuff better. You pick the right one for the job. See [page 5](5-why-its-built-this-way.md).

### The autocomplete brain — the FIM model
- **Who it is:** a tiny model (*Qwen2.5‑Coder‑3B* — only 3 billion knobs).
- **The job:** as you type code in your editor, it instantly finishes your line — like the predictive text on your phone, but for code. ("FIM" just means *Fill‑In‑the‑Middle* — see the [glossary](6-glossary.md).)
- **Why tiny:** autocomplete has to be *instant*, and it has to share the graphics card with the everyday brain at the same time. Small and fast beats big and slow here. It types about **292 words/sec** and sips only a little of the card's memory.

---

## 🚂 The Engine Room — llama.cpp + llama-swap

A brain (the AI model) is just a giant file sitting on your disk. Something has to actually **run** it — load it onto the graphics card and feed it your questions. That's the engine room, and it's two tools working together:

- **llama.cpp** is *the engine* — the program that takes a brain file and runs it on the graphics card, as fast as the hardware allows.
- **llama-swap** is *the receptionist*. You have three brains but can't run them all at full size at once (not enough desk space — see VRAM below). So the receptionist sits at the front desk with one phone number, and when you ask for "the fast brain," it puts the fast brain on the desk; ask for "the big brain," it swaps them. The rest of DokiCode only ever has to call **one phone number** and say which brain it wants.

> 🗄️ **Why "desk space" matters (VRAM).** A graphics card has a fixed amount of super‑fast memory called **VRAM** — here, 32 GB. A brain has to physically fit in VRAM to run fast, like papers fitting on a desk. The everyday brain fills about half the desk; the big brain doesn't fit at all and has to stack overflow papers on a slower shelf nearby (your RAM). This "what fits on the desk" math is the single biggest constraint in the whole project.

---

## ✋ The Hands — the harness (Crush)

The brain can *think*, but it can't *touch* anything. It can't open a file or run a test on its own — it's just a very clever text predictor. The **harness** is the body around the brain: the eyes and hands.

DokiCode uses a harness called **Crush**. When you give DokiCode a task, Crush:

- 👀 **Shows the brain your files** when it needs to read them
- ✍️ **Makes the edits** the brain decides on
- 🏃 **Runs the commands** (build, test) the brain asks for, and shows the brain the results
- 🛑 **Asks your permission** before doing anything risky, like deleting a file or running a command

The way the brain "asks" the hands to do something is called a **tool call** — basically the brain filling out a little request form: *"Please run `dotnet test` and tell me what happens."* The hands carry it out and report back. This back‑and‑forth, many times in a row, is how a task gets done. [Page 3](3-a-task-step-by-step.md) shows it in slow motion.

> 🤝 There's a backup set of hands too, called **OpenCode**. DokiCode held a little contest between them (page 5) and Crush won the daily‑driver job, but OpenCode is kept around as a challenger.

---

## 📇 The Library Card — web search

Sometimes the brain needs to know something that happened after it was built — today's news, a library's latest version. So DokiCode gives it a **library card**: a connection to **DuckDuckGo** web search.

When the brain hits a question it can't answer from memory, it makes a tool call — *"search the web for X"* — and the hands fetch the results. Crucially, **only your search words go out, never your code,** and it's plain search, not an AI service in the cloud. It's the same as the robot quickly Googling something for you.

The tech that connects an outside tool (like search) to the robot is called **MCP** — think of it as a *universal adapter* for plugging tools into AI assistants. DokiCode deliberately plugs in **just the one** (search), because — funny but true — giving a smaller brain *too many* tools makes it worse at picking the right one. Fewer, sharper tools beats a cluttered toolbox. (More on that on [page 5](5-why-its-built-this-way.md).)

---

## 🏠 The house rules — `AGENTS.md`

One more piece, and it's low‑tech but powerful. In each project you work on, you leave the robot a short note called **`AGENTS.md`** — house rules. Things like *"the test command is `dotnet test`,"* *"always run the tests before saying you're done,"* and *"make the smallest change that works."*

Why it matters: a smaller brain is more **literal** and needs the ground rules spelled out, where a giant cloud brain might guess them. A good `AGENTS.md` is like a sticky note on the new assistant's desk — it's the single cheapest way to make DokiCode dramatically more reliable.

---

## Putting it together

| The part | The analogy | The real tool |
|----------|-------------|---------------|
| The brains | The crew (fast / careful / autocomplete) | Qwen3‑Coder‑30B, gpt‑oss‑120b, Qwen2.5‑Coder‑3B |
| The engine room | The engine + the receptionist | llama.cpp + llama‑swap |
| The hands | The body that touches things | Crush (backup: OpenCode) |
| The library card | A quick web lookup | DuckDuckGo via MCP |
| The house rules | A sticky note for the assistant | `AGENTS.md` |
| The desk | How much fits at once | 32 GB VRAM on the RTX 5090 |

Now let's watch all of this work together on a real job. →

---

← [The Big Idea](1-the-big-idea.md) · [Home](Home.md) · Next: [Watch It Solve a Task](3-a-task-step-by-step.md) →
