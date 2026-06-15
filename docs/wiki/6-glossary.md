# 6. Glossary — every nerdy word, explained like you're five

← [Why It's Built This Way](5-why-its-built-this-way.md) · [Home](Home.md) · Next: [Quick Start](7-quick-start.md) →

---

Every piece of jargon you might bump into, in plain English. Skim it, or look things up as you go.

### The big concepts

**AI model (the "brain")**
The thing that does the thinking. It's a giant file full of patterns learned from reading tons of code and text. On its own it just predicts text really well; the harness turns that into useful actions.

**Parameters (the "knobs")**
The little learned settings inside a model. More parameters → more nuance and capability (and a bigger file). "30B" means 30 *billion* of them. Cloud brains have far more; that's the main reason they're smarter.

**Agent**
An AI that doesn't just chat — it *does things*. It reads files, edits code, runs commands, and loops until the job's done. DokiDex is an agent.

**Harness**
The "body" around the brain — the program that gives it eyes and hands. It shows the brain your files, makes the edits the brain decides on, runs commands, and handles permissions. DokiDex's harness is **Crush**.

**Local vs. cloud**
*Local* = runs on your own computer; nothing leaves. *Cloud* = runs in a company's data center; your data is sent there. DokiDex is local (except optional plain web search).

**Inference**
A fancy word for "running the model to get an answer." When the brain is thinking, it's "doing inference."

### The hardware words

**VRAM (the "desk space")**
The super‑fast memory *on the graphics card* — 32 GB here. A brain has to fit in VRAM to run fast. This "what fits on the desk" limit is the single biggest constraint in DokiDex.

**RAM (the "back shelf")**
Your computer's regular memory — bigger (64 GB) but slower than VRAM. When a brain is too big for the card, the overflow spills into RAM, which is why the big brain runs slower.

**GPU / graphics card**
The chip that's freakishly good at the math AI models need. Here it's an **RTX 5090**. It's the engine the whole thing runs on.

### The model words

**GGUF**
Just a file format for a downloadable brain — the kind of file you grab and run locally. If a model is "available as a GGUF," DokiDex can use it.

**Quantization (a "quant")**
Squishing a brain to make it smaller so it fits on the card — like saving a photo as a slightly compressed JPEG. You lose a tiny bit of quality for a big space saving. "Q4," "Q8" describe how much it's squished (lower number = more squished).

**MoE (Mixture of Experts)**
A brain design where, for each question, only the few relevant "experts" inside wake up instead of the whole thing. It's how DokiDex runs a 120‑billion‑knob brain without needing a data center — most of it stays asleep at any moment, so it's cheaper to run than its size suggests.

**Context window**
How much the brain can "hold in its head" at once — the current conversation plus the files it's looking at. Measured in *tokens*. DokiDex's is large (~128k tokens), but it still fills up on long sessions, which is why the advice is "one task per chat."

**Token**
The little chunk a model reads and writes in — roughly ¾ of a word. "265 tokens/second" ≈ 265 words‑worth of code per second. Speeds and context sizes are measured in tokens.

**FIM (Fill‑In‑the‑Middle)**
The special skill behind autocomplete: given the code *before* and *after* your cursor, guess what goes in the gap. DokiDex uses a tiny FIM model so it can finish your lines instantly as you type.

### The plumbing words

**llama.cpp**
The *engine* — the open‑source program that actually runs a brain (a GGUF file) on your graphics card, fast.

**llama-swap**
The *receptionist* — sits in front of the engine with one phone number and swaps which brain is loaded when you ask for a different one. Lets DokiDex juggle three brains through a single connection.

**Endpoint**
The "phone number" other parts of the system call to talk to the brain (here, an address like `localhost:8080`). "OpenAI‑compatible endpoint" just means it speaks the same common language most AI tools expect, so they plug in easily.

**Tool call**
How the brain asks the hands to *do* something. It fills out a structured request — *"run this command,"* *"edit this file"* — and the harness carries it out and reports back. Reliable tool calls are what make an agent actually work.

**MCP (Model Context Protocol)**
A *universal adapter* for plugging outside tools into an AI assistant. DokiDex uses it to plug in exactly one tool: web search.

**Prompt cache**
A speed trick. Each turn, the brain technically re‑reads the whole conversation; the cache lets the engine remember the earlier part instead of re‑processing it every time — like keeping your place with a bookmark. Keeps long sessions snappy.

### The tools by name

**Crush**
DokiDex's hands (the harness) — the daily driver. A Windows‑friendly program that connects the brain to your files, terminal, and permissions. Won the bake‑off.

**OpenCode**
The runner‑up harness, kept around as a challenger to Crush.

**DuckDuckGo MCP**
The web‑search tool, plugged in via MCP. Keyless (no signup), and it's plain search — not an AI cloud service. The only thing in DokiDex that touches the internet.

**llama.vscode**
The VS Code extension that shows the autocomplete suggestions (from the FIM brain) inside your editor.

### The workflow words

**`AGENTS.md`**
A short "house rules" note you leave in each project: the test command, the code style, "always run the tests before saying you're done." The cheapest way to make the robot reliable.

**Eval / golden‑task suite**
The robot's "driving test" — a fixed set of real coding jobs, each with an automatic pass/fail check. Re‑run after any change to prove things got better, not worse. DokiDex's score: 10/11 (91%).

**Headless**
Running the robot automatically with no human watching — used for the driving test. In this mode it acts *without* asking permission, which is why it's only ever pointed at throwaway copies.

**Bake‑off**
A head‑to‑head contest to pick a tool by measured results instead of opinion. DokiDex used one to choose Crush over OpenCode.

**Git**
A system that snapshots your code so you can rewind to any earlier version. DokiDex's safety rule: only ever let the robot loose on code that's saved in Git — it's the undo button.

---

That's the whole vocabulary. If a word shows up anywhere in the wiki and it's not here, it's probably explained right where it appears.

← [Why It's Built This Way](5-why-its-built-this-way.md) · [Home](Home.md) · Next: [Quick Start](7-quick-start.md) →
