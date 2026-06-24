# AI harnesses & models — research for the DokiDex stack

> **Sourcing convention:** [Confirmed] = primary source (paper, official docs, model card, verified arXiv). [Reported] = secondary analysis, vendor claim, or aggregated benchmark site. [Proprietary/Unknown] = internal evaluation or unverifiable vendor disclosure. [DokiDex] = verified directly against this repo's code, config, or eval results.

---

## 1. Executive summary

Three independent research threads converge on the same finding: **the harness is the primary differentiator, not the weights**. The same model under different scaffolding shows a 22+ point swing on SWE-bench Pro. [Confirmed — particula.tech] On a single 32GB RTX 5090, DokiDex's highest-leverage investments are in the C# Chat/LocalLlm/ChatTools loop and the llama-swap.yaml serving config — not model swaps.

**The DokiDex stack today** (as of 2026-06-23, `serving/llama-swap.yaml` b9616):

| Slot | Model | Quant / Format | VRAM at 131k ctx | Status |
|---|---|---|---|---|
| `coder-fast` | Qwen3-Coder-30B-A3B | UD-Q4_K_XL | ~26 GB | Live |
| `coder-fast-lite` | Qwen3-Coder-30B-A3B | UD-Q4_K_XL | ~21 GB (65k ctx) | Live |
| `coder-big` | gpt-oss-120b | MXFP4, `--n-cpu-moe 22` | ~27 GB GPU + ~40 GB RAM | Live |
| `fast-candidate-gptoss20b` | gpt-oss-20b | MXFP4 | ~17.9 GB | On-demand (eval-partial) |
| `vision` | Qwen3-VL-32B | Q4_K_M + mmproj F16 | ~30.7 GB | Live (promoted 2026-06-21) |
| `vision-8b` | Qwen3-VL-8B | Q4_K_M + mmproj F16 | ~7 GB | Live (fallback) |

**The five highest-ROI actions** (all feasible, ordered by impact-per-effort):

1. Add `setParams` + model aliases per call-mode in llama-swap.yaml — zero C# changes needed, immediate tool-call reliability improvement.
2. Enable MTP speculative decoding (`--draft-max 8`) on `coder-fast` — potential 2–3× decode speedup with no quality loss.
3. Strip `reasoning_content` from gpt-oss turn history in the C# loop — prevents context pollution.
4. Add JSON-schema grammar enforcement on tool-argument extraction requests — near-100% parse reliability.
5. Implement context compaction at 70% of ctx-size — prevents context drift in long agentic sessions.

---

## 2. AI agent-harness architecture

### 2.1 The four necessary and sufficient elements

An agent harness requires exactly four components: an **agent loop**, a **tool interface**, **context management**, and **control mechanisms**. [Confirmed — augmentcode.com, arxiv 2603.05344] Everything else is optimization. DokiDex's C# Chat/LocalLlm/ChatTools loop covers all four.

### 2.2 The agent loop: ReAct vs plan-execute

**ReAct (Reasoning + Acting)** is the dominant production pattern and what DokiDex implements. Each iteration: assemble context → call model (with optional chain-of-thought) → parse `tool_use` blocks → execute → append results → repeat until done. Claude Code, Codex CLI, OpenHands, and Aider all use this core. [Confirmed — arxiv 2604.14228]

A production loop typically adds 3–6 phases per iteration:

1. Pre-check / context compaction
2. Optional thinking / chain-of-thought
3. Optional self-critique
4. LLM call with full tool schemas
5. Tool execution with approval gate
6. Post-processing / termination decision

[Confirmed — arxiv 2603.05344]

**Weakness of pure ReAct:** 35% more input tokens per task vs plan-execute on comparable benchmarks; latency accumulates across many turns. [Confirmed — dev.to/jamesli]

**Plan-Execute** separates a read-only Planner (often larger/slower) from an Executor (smaller/faster). Decoupling allows a cheaper executor, easier recovery by re-planning on failure, and schema-level tool restriction (planner gets no write tools). Measured gains: +5–10 points SWE-bench on complex multi-file tasks. [Reported — particula.tech]

> **DokiDex:** The llama-swap config already has the right ingredients: `gpt-oss-20b` (reasoning/planning role) + `coder-fast` (execution). A two-phase harness dispatching plan → `coder-fast` is an immediately actionable improvement — keep the planner's tool schema read-only. Implement as a call-mode flag in the C# loop, not a structural rewrite.

### 2.3 Tool-use architecture patterns

**Pattern 1: Separation of reasoning and enforcement.** The model decides what it wants; a separate code path decides if it is allowed. The model emits structured `tool_use` blocks; the harness validates, gates, and executes. A misbehaving model cannot override safety checks because enforcement is a different code path. [Confirmed — arxiv 2604.14228, augmentcode.com]

**Pattern 2: Concurrent read / serial write.** Read-only tool operations (file reads, searches, linting) execute in parallel. State-mutating operations (file writes, shell exec, git) serialize. Claude Code implements a sibling abort controller — if any Bash tool errors, sibling processes terminate. [Confirmed — arxiv 2604.14228]

> **DokiDex:** Apply this to ChatTools: fan-out read operations (grep, code_search, search_library) concurrently; serialize write/exec operations.

**Pattern 3: Schema-level filtering.** Restricting tools by removing them from the schema sent to the model — not runtime permission checks — proved more reliable for restricted modes. [Confirmed — arxiv 2603.05344]

**Pattern 4: Lazy MCP tool discovery.** Eagerly loading all MCP tool schemas into every prompt wastes tokens. Production systems lazy-load schemas only when a tool in that namespace is invoked. [Confirmed — arxiv 2603.05344] DokiDex's three-tool curated set already minimizes this problem.

**Pattern 5: Speculative tool execution.** Predict likely tool invocations before the model finishes generating, execute speculatively in parallel, validate against actual model output. Most effective when tool latency >> model generation latency. [Confirmed — arxiv 2512.15834] With CPU-offloaded `coder-big` (slow generation), this is directly applicable for high-latency tools.

### 2.4 Context and memory management

**The binding constraint:** Across all surveyed systems, context window budget is treated as the primary engineering constraint, not model capability. [Confirmed — arxiv 2604.14228] Naive full-context caching can *degrade* latency by 8.8% due to cache overhead without hits. [Confirmed — arxiv 2601.06007]

**Read operations dominate the token budget:** Coding agents spend 76.1% of their token budget on read operations (file reads, directory listings, search) rather than write/reasoning operations. Context pruning targeting read results is the highest-leverage intervention. [Confirmed — arxiv 2601.16746]

**Claude Code's 5-layer progressive compaction** (best-documented production implementation; runs before every model call, cheapest layers first):

| Layer | Technique | Cost |
|---|---|---|
| 1. Budget reduction | Per-message size limits on tool results | Near-zero |
| 2. Snip | Temporal trim of older history segments | Low |
| 3. Microcompact | Fine-grained compression, cache-aware | Medium |
| 4. Context collapse | Read-time projection without mutating stored state | Medium |
| 5. Auto-compact | Full model-generated semantic summary | High |

[Confirmed — arxiv 2604.14228]

**Active context compression (Focus architecture):** Agent-initiated compression — call `start_focus` at a checkpoint, do work, then call `complete_focus` to summarize the segment and delete raw exploration logs. Measured: **22.7% token reduction** (14.9M → 11.5M tokens) at identical task success (60%). Passive compression only achieved 6% savings with accuracy losses. [Confirmed — arxiv 2601.07190]

> **DokiDex:** Implement budget-reduction at the tool-result layer first (cheapest). Truncate grep/file-read outputs to configurable limits before appending to context. This alone addresses the 76% read-token problem. Auto-compact (full summary) is last-resort only. Track token count per turn; trigger summarization at 70% of ctx-size (91k tokens for the 131k context models).

### 2.5 Prompt caching / KV-cache reuse

**Confirmed savings on agentic workloads** [Confirmed — arxiv 2601.06007]:

| Provider | Cost Reduction | TTFT Reduction |
|---|---|---|
| Anthropic (Claude Sonnet 4.5) | 78.5% | −22.9% |
| GPT-5.2 | 79.6% | −13.0% |
| GPT-4o | 45.9% | −30.9% |

**What breaks cache hits:** timestamps or session IDs in the system prompt; changing tool sets between requests; dynamic data embedded before the conversation history. For llama.cpp, the equivalent is `--cache-reuse` / KV-cache slot reuse — the prefix must be bit-identical across requests.

> **DokiDex:** `--cache-reuse 256` is already set on `coder-fast`, `coder-fast-lite`, and `coder-big`. [DokiDex] This is correct. The C# ChatTools loop must ensure the system prompt is **byte-stable** across turns — no dynamic timestamps, no random IDs injected into the system prompt text. Tool schema definitions should be templated and frozen at startup.

### 2.6 Structured output and tool-call reliability

**Grammar-constrained decoding (GBNF):** llama.cpp implements GBNF (GGML Backus-Naur Form) for constrained sampling. During sampling, `llama_grammar_apply_impl()` zero-masks tokens that would violate the grammar. JSON schemas for tool calls are auto-converted to GBNF. The **autoparser** approach triggers grammar enforcement only after a specific token (e.g., `<tool_call>`), allowing free-form preamble then enforcing structured output — preserves reasoning fluency while guaranteeing parse-valid tool calls. [Confirmed — deepwiki.com/ggml-org/llama.cpp/8.1]

> **DokiDex:** Add `response_format: {type: "json_schema", json_schema: ...}` to completion requests during tool-argument extraction passes. This eliminates tool-call parse failures. Keep grammar enforcement off for general chat turns to preserve natural responses.

### 2.7 Sub-agent orchestration

**Isolation pattern** (all production multi-agent systems converge on this):
- Subagents receive only a **summary** of parent context, not the full history
- Each subagent writes to a sidechain transcript; content does not inflate parent context
- Subagents get filtered tool schemas
- Fresh context window per subagent invocation

[Confirmed — arxiv 2604.14228, arxiv 2603.05344]

**Measured gains from parallelism:** Latency-supervised scheduling reduces critical-path length by **38–46%** vs sequential approaches at comparable task success. [Confirmed — arxiv 2601.10560]

**When single-agent wins:** Multi-agent overhead (context summaries, coordination, spawn cost) makes it worse than single-agent for short tasks. DokiDex's interactive chat-driven loop is correctly single-agent; subagent spawning is appropriate only for delegating self-contained sub-tasks (e.g., "run tests", "scan this file for X").

### 2.8 SWE-bench: scaffold >> model (the key empirical finding)

**Same model, different scaffold → 22+ point swing on SWE-bench Pro.** [Confirmed — particula.tech]

Specific data points:
- Claude Opus 4.5: 45.9% (minimal scaffold) → 55.4% (Claude Code scaffold) on SWE-bench Pro
- Six frontier models cluster within 0.8 points of each other under identical scaffolding
- Grok Code Fast: 6.7% → 68.3% by changing **edit tool format alone**
- Anthropic vendor-reported 69.2% (bespoke scaffold) vs 51.9% (standardized SEAL board) — 17.3 point gap from scaffold difference alone

High-impact scaffold techniques ranked by measured SWE-bench gain:
1. Structured error recovery (rollback + retry): +5–15 points
2. Planning-execution separation: +5–10 points
3. Context compaction: +3–8 points
4. Tool orchestration (parallel ops): +2–4 points
5. Persistent memory: +2–5 points

[Confirmed — particula.tech, digitalapplied.com]

> **DokiDex:** Error recovery (rollback + retry on tool failure) and planning-execution separation are the two highest-ROI investments. A structured 1–2 retry loop when tool-call JSON fails to parse, feeding the parse error back as a user message, is implementable in the C# loop today.

---

## 3. How the models are built

> Accuracy note: architecture and training data for **open models** (Qwen3, gpt-oss, DeepSeek) is confirmed from primary sources. **Proprietary models** (Claude, GPT-5.x) disclose nothing meaningful — treat all claims as marketing.

### 3.1 Architecture landscape: dense vs MoE

The dominant split is between **dense transformers** (every parameter active per token) and **Mixture-of-Experts (MoE)** architectures (only a fraction of parameters active per token). All major open models above ~30B have migrated to MoE because the activation-to-parameter ratio delivers comparable quality at a fraction of the inference cost.

| Model | Type | Total Params | Active/Token | Context |
|---|---|---|---|---|
| Qwen3-Coder-30B-A3B | MoE | 30.5B | 3.3B (128 experts, 8 active) | 256K native / 1M YaRN |
| gpt-oss-20b | MoE | 20.9B | 3.6B (32 experts) | 128K |
| gpt-oss-120b | MoE | 116.8B | 5.1B (128 experts) | 128K |
| GLM-4.7-Flash | MoE | 106B total | 12B active | 128K |
| DeepSeek-V4 Pro | MoE | ~1.6T | ~37B active | 1M |
| Claude Fable 5 | Proprietary | Unknown | Unknown | Claimed millions |
| GPT-5.5 | Proprietary | Unknown | Unknown | 1.05M |

[Confirmed for open models — HuggingFace model cards, arxiv 2508.10925 (gpt-oss), arxiv 2505.09388 (Qwen3)]

> **DokiDex:** On 32GB VRAM, gpt-oss-120b (5.1B active) and Qwen3-Coder-30B (3.3B active) are the practical sweet spot — MoE means full-quality inference with a fraction of the memory a dense model would need. The MXFP4 format of the gpt-oss weights is load-bearing for the 32GB fit.

### 3.2 Attention variants

**Grouped Query Attention (GQA)** is now the universal standard for open models. GQA reduces KV cache by sharing K/V heads across groups of Q heads. [Confirmed — HuggingFace model cards]

- Qwen3-30B-A3B: 32 Q heads, 4 KV heads (8:1 grouping)
- gpt-oss: grouped multi-query attention, group size 8, with RoPE positional encoding

**Multi-head Latent Attention (MLA)** is DeepSeek's proprietary innovation — compresses KV representations via low-rank projection, achieving only 70 KB/token KV cache vs. 192–328 KB/token for GQA equivalents. [Confirmed — arxiv 2412.19437] GLM-4.7-Flash uses DeepSeek's MLA architecture (`deepseek2`/`Glm4MoeLiteForCausalLM`) — which is why it's listed in llama-swap.yaml as requiring the `deepseek2` runtime arch, not `glm4moe`. [DokiDex — decisions.md]

### 3.3 Pre-training data

**Qwen3** [Confirmed — arxiv 2505.09388]: 36 trillion tokens, 119 languages, 3-stage curriculum: (1) ~30T general tokens at 4K seqlen; (2) ~5T STEM/coding/reasoning-heavy stage; (3) hundreds of billions at 32K+ using YaRN + Dual Chunk Attention.

**Qwen3-Coder-30B** [Confirmed — qwenlm.github.io/blog/qwen3-coder]: 7.5T token pre-training corpus, 70% code ratio. **This is a separate model from base Qwen3** — specialized pre-training, not just post-training on the base.

**DeepSeek-V3** [Confirmed — arxiv 2412.19437]: 14.8T tokens. FP8 mixed-precision training throughout (first at this scale). Training cost: ~250 GFLOP/token — vs. 2,448 for equivalent dense LLaMA-405B.

**gpt-oss** [Confirmed — arxiv 2508.10925]: Focused on STEM, coding, and general knowledge. Natively trained and post-trained with MXFP4 quantization of MoE layer weights. 36-layer (120b) / 24-layer (20b) transformer with alternating dense and locally-banded sparse attention.

**Claude Fable 5 / GPT-5.5** [Proprietary/Unknown]: Training data, parameter count, and architecture not disclosed. Treat all capability claims as marketing.

### 3.4 Post-training: RLVR and reasoning emergence

**RLVR (Reinforcement Learning with Verifiable Rewards)** is now the dominant post-training paradigm for reasoning models. Rather than learning from human preference labels, the model is rewarded for producing verifiably correct outputs — math answers checked by a solver, code checked by execution.

**DeepSeek-R1** [Confirmed — arxiv 2501.12948]: Uses GRPO (Group Relative Policy Optimization) — a variant of PPO evaluating outputs relative to a group baseline. Key confirmed finding: extended chain-of-thought behavior **emerges from RL optimization alone** without being explicitly taught. Models develop self-verification, error correction, and backtracking organically.

**Qwen3** [Confirmed — arxiv 2505.09388]: 4-stage post-training: (1) Long-CoT cold start; (2) Reasoning RL with GRPO; (3) Thinking/non-thinking mode fusion via SFT; (4) General-domain RL. The dual-mode approach (thinking vs. non-thinking in a single model) is controlled at inference via `/think` / `/no_think` tokens in the chat template.

**Qwen3-Coder-30B** [Confirmed — qwenlm.github.io]: Adds **Agent RL** — multi-turn long-horizon RL where the model interacts with real software environments (20,000 parallel environments). Reward = task completion, not just answer correctness. This is how agentic tool-use behavior is baked into weights, not just prompted.

> **DokiDex:** The `fast-candidate-gptoss20b` was post-trained with "a high-compute RL stage" and CoT/tool-use emphasis — this is why it achieves 98.7% AIME at high reasoning budget while being only 20.9B total. [DokiDex — decisions.md: 14/15 (93%) on the golden gate]

### 3.5 Distillation

**DeepSeek-R1 distillation** [Confirmed — arxiv 2501.12948]: DeepSeek generated 800,000 reasoning-trajectory examples from R1, then used them to SFT-fine-tune Qwen and Llama base models. The distilled models achieve competitive reasoning performance without any RL — the structured thinking format alone transfers.

> **DokiDex:** If domain-specific fine-tuning of gpt-oss-20b is ever wanted, the distillation approach (generate synthetic CoT data from coder-big, SFT the smaller model) is the proven and cheap path.

### 3.6 Quantization and inference efficiency

**GGUF quantization perplexity vs. throughput** [Confirmed — arxiv 2601.14277]:

| Format | Size vs FP16 | Perplexity (WikiText-2) | Notes |
|---|---|---|---|
| Q8_0 | −47% | 7.33 (≈ FP16) | Lossless for most purposes |
| Q5_K_M | −64% | 7.40 | Slight quality gain over Q4 at moderate cost |
| Q4_K_M | −69% | 7.56 | Community "sweet spot": ~3.5% quality loss |
| IQ4_XS | ~−72% | Similar to Q4_K_M | Importance-matrix–calibrated; better per-byte quality when imatrix available |

Key nuance: Q4 → Q5 noticeably helps on **coding and complex reasoning** specifically. The **UD-Q4_K_XL** format used in `coder-fast` is a per-quant-group "XL" variant with importance matrix — slightly above Q4_K_M quality, correctly chosen. [DokiDex]

**IQ variants (importance-matrix quants):** The IQ family reconstructs weights using a calibration pass over representative data, selectively protecting the most influential weights. IQ4_XS typically matches or beats Q4_K_M quality at smaller size. [Confirmed — kaitchup.substack.com]

**NVFP4/MXFP4 (Blackwell-native):** RTX 5090 supports hardware FP4 tensor cores. NVFP4 delivers 1.6× throughput over BF16 with 41% energy reduction. However: NVFP4 requires TensorRT-LLM — it is **not** a GGUF format, and llama.cpp loads it as a dequantized pass. The `gpt-oss-120b-mxfp4` files in DokiDex are MXFP4 GGUFs from ggml-org — these are dequantized during loading by llama.cpp. A GGUF Q4_K_XL repack of gpt-oss-120b might decode faster due to pure bandwidth advantage and is worth measuring. [Confirmed — developer.nvidia.com/blog/nvfp4, arxiv 2601.09527] [DokiDex]

**Speculative decoding:** EAGLE-3 achieves 80%+ acceptance rates on high-predictability tasks (code, templates) with 2–5× real-world speedup via a single Transformer layer + LM head as draft model. [Confirmed — sesamedisk.com] Qwen3-Coder-30B-A3B includes MTP (Multi-Token Prediction) heads — PR #22673 adds MTP support to mainline llama.cpp. DokiDex runs b9616 — check whether #22673 is included. [Confirmed — dredyson.com]

---

## 4. Per-model comparison

> Benchmark accuracy: [B] = benchmark-backed (primary source cited). [M] = marketing claim from vendor. [I] = community impression.

### 4.1 Quick-reference table

| Model | Coding | Agentic/Tool-use | Reasoning | Long ctx | Vision | Local @ 32 GB |
|---|---|---|---|---|---|---|
| **Claude Fable 5** | SWE-bench Pro 80.3% [B] | Best self-validation loop [B] | GPQA-Diamond 94.1% [B] | 1M ctx; GraphWalks-BFS 91.1%@256K [B] | Text-native only | NOT available |
| **Claude Opus 4.8** | SWE-bench 88.6%, Pro 69.2%, MCP-Atlas 82.2% [B] | Dynamic workflows; 4× less silent code-flaw passing [B] | HLE-with-tools 57.9% [B] | 1M ctx, 128K output [B] | Text-native only | NOT available |
| **GPT-5.5** | SWE-bench 88.7%, Pro 58.6% [B] | Terminal-Bench 2.0 82.7%; CyberGym 81.8% [B] | ARC-AGI-2 85% [B] | 1.05M ctx; MRCR@512K-1M 74.0% [B] | Omnimodal [M] | NOT available |
| **gpt-oss-20b** | SWE-bench ~37–61% (budget-dependent) [B] | MoE + tool-calling; 3.6B active | AIME 2025 57.5%→98.7% (low→high budget) [B] | 128K native [B] | None | YES — ~13 GB MXFP4 [B] |
| **gpt-oss-120b** | SWE-bench 48–62% (budget-dependent) [B] | Same agentic features; 5.1B active | AIME 2025 73–98% across budgets [B] | 128K native [B] | None | PARTIAL — CPU offload required [B] |
| **Qwen3-Coder-30B-A3B** | Top open-source SWE-bench without test-time scaling [M] | Agent RL post-trained; 256K ctx; non-thinking | Non-thinking only; limited pure-reasoning | 256K native, 1M YaRN [B] | None | YES — Q4_K_XL ~17.7 GB [B] |
| **Qwen3-VL-32B** | Text coding parity with DeepSeek-V3 tier [M] | Top OSWorld agentic scores [M] | AIME25, MMLU competitive [M] | 256K native, 1M YaRN [M] | Design2Code 92.0, ChartMimic 80.5 [B] | YES (tight) — Q4_K_M ~18.4 GB + 1.1 GB mmproj [DokiDex] |
| **GLM-4.7-Flash** | SWE-bench 59.2% [B] | Tau2-Bench 79.5% [B] | AIME 91.6, GPQA 75.2 [B] | 128K ctx [B] | None | YES — ~17.5 GB UD-Q4_K_XL [B] |
| **GLM-4.6 / 4.7 (full)** | LiveCodeBench 82.8% / SWE-bench 73.8% [B] | Three thinking modes [B] | HLE-with-tools 42.8% [B] | 200K / 131K [B] | None | NOT practical (135+ GB at 2-bit) [B] |
| **DeepSeek V4 Pro** | #27/311 coding index [B] | Strong tool-use; 1M ctx | #20/374 reasoning [B] | 1M ctx [B] | None | NOT runnable (~1.6T MoE) [B] |
| **DeepSeek R1-Distill-Qwen-32B** | SWE-MERA 13.2% pass@1 [B] | Limited; single-turn reasoning chains | Beats o1-mini on AIME/MATH [B] | 128K [B] | None | YES — Q4 ~18 GB [B] |

### 4.2 Per-model deep-dives

#### Claude Fable 5 — frontier cloud-only (not in DokiDex rotation)
API-only at $10/$50 per M tokens. SWE-bench Pro 80.3% vs Opus 4.8's 69.2% — the difference between completing a multi-day engineering task and losing the plan. GraphWalks-BFS at 256K: 91.1% vs 73.7% best competitor [vellum.ai]. Safeguard classifiers redirect sensitive queries to Opus 4.8 (< 5% of sessions).

**Weakness:** API-only, not locally runnable, high cost, safeguard false-positive rate in chemistry/biology is a known complaint.

**DokiDex role:** Cloud escalation tier only, not in llama-swap rotation.

---

#### Claude Opus 4.8 — best cloud-hosted all-rounder
Best honest agentic coding model at its price point. 4× less silent code-flaw passing, 17× less dishonest agentic summaries vs Sonnet 4.6 [anthropic.com]. MCP-Atlas 82.2%, BrowseComp 84.3% [openrouter.ai]. Dynamic workflows (parallel subagents) for complex multi-agent pipelines.

**Weakness:** API-only. Trails Fable 5 by 11 points on SWE-bench Pro.

**DokiDex role:** Best fit for tasks that exceed local model capability but don't need Fable 5's frontier benchmarks.

---

#### GPT-5.5 — best for terminal and long-context retrieval
Terminal-Bench 2.0 82.7% — a 13-point lead over Opus 4.7 [vellum.ai]. MRCR long-context retrieval at 512K-1M: 74.0% vs 32.2% (Opus 4.7) [vellum.ai]. ARC-AGI-2 85% [vellum.ai]. Omnimodal.

**Weakness:** SWE-bench Pro 58.6% — 10 points behind Opus 4.8 on production repo-scale tasks. Multimodal ranking only #59/124 on grounded vision tasks [benchlm.ai]. 29% false task-completion rate per OpenAI's own evals [vellum.ai blog].

**DokiDex role:** If the C# tool loop dispatches heavy bash/terminal steps, GPT-5.5 is meaningfully better than Opus 4.8 on that sub-task class. For pure coding PRs, Opus 4.8 wins.

---

#### gpt-oss-20b — DokiDex's reasoning workhorse (local)
20.9B total / 3.6B active MoE. AIME 2025 at high-reasoning budget: 98.7% — nearly matching gpt-oss-120b [arxiv 2508.10925]. Fits in ~12.8 GB VRAM in native MXFP4, leaving 19+ GB free for KV cache on 32 GB. Apache 2.0. Native llama.cpp + GGUF support. **DokiDex eval gate: 14/15 (93%) on the golden gate (2026-06-21).** [DokiDex — evals/results.jsonl, decisions.md]

**Weakness:** SWE-bench at low reasoning budget is only 37% — requires high-budget reasoning mode, which is slower. No vision. 128K context (not 256K). Instruction following ranked #98/124 [benchlm.ai] — can drift from complex instructions. **Known issue: gpt-oss puts chain-of-thought in `reasoning_content` — give it a normal `max_tokens` or visible content can come back empty.** [DokiDex — llama-swap.yaml comment]

**DokiDex role:** Reasoning slot. Reasoning budget scaling is the primary tuning lever — high budget closes the gap to gpt-oss-120b on math/AIME; medium budget is the practical sweet spot for chat-loop latency.

---

#### gpt-oss-120b — deeper reasoning at the cost of CPU offloading
116.8B total / 5.1B active, 128 experts. AIME 2025 at high budget: 97.9% [arxiv 2508.10925]. SWE-bench at high budget: 62.4% vs 60.7% for 20b — converging [smythos.com].

**Weakness:** ~61 GB weights — cannot fit in 32 GB VRAM alone. Must use `--n-cpu-moe 22` + CPU RAM offloading → 15–30 t/s depending on DDR5 bandwidth. Context should be capped at 32K to leave KV-cache headroom.

**DokiDex role:** "coder-big CPU-offloaded" slot. The MoE offload pattern with 64 GB DDR5 is the right lever. Consider whether gpt-oss-20b at high budget is good enough for most tasks before loading 120b. [DokiDex — llama-swap.yaml]

---

#### Qwen3-Coder-30B-A3B — best local-only coding specialist
30.5B total / 3.3B active MoE. 256K native context (the widest in the local stack). Trained with Agent RL for multi-turn tool loops. Q4_K_XL ~17.7 GB VRAM — fits 32 GB with ample KV-cache headroom. Tool calling via `--jinja` in llama.cpp confirmed. [DokiDex — llama-swap.yaml, decisions.md]

**Weakness:** Non-thinking mode only — no chain-of-thought; cannot be prompted into a reasoning trace. Slightly slower TTFT (2.72 s vs 1.92 s median) [artificialanalysis.ai].

**DokiDex role:** "coder-fast" daily driver. The 256K context is a structural advantage over gpt-oss-20b (128K) for large-repo tasks. Supports MTP heads — check if b9616 includes PR #22673 for speculative decoding.

---

#### Qwen3-VL-32B — local vision-language workhorse
Dense 32B vision model. Design2Code 92.0, ChartMimic 80.5 [qwen3-vl.com]. Top OSWorld for visual GUI agents [search results]. Q4_K_M ~18.4 GB + mmproj ~1.1 GB fits 32 GB VRAM (Q8_0 at 32.4 GB does NOT). [DokiDex — decisions.md: promoted 2026-06-21]

**Weakness:** Q8_0 confirmed to not fit 32 GB with mmproj — Q4_K_M is the hard ceiling. No specific SWE-bench data for 32B variant. Vision inference blocks GPU from LLM use (DokiDex mutex). `--cache-reuse` is NOT set in the current `vision` config. [DokiDex — llama-swap.yaml]

**DokiDex role:** Vision slot. Run at Q4_K_M to leave headroom for KV cache. Every vision call evicts the current LLM — keep vision tasks batched when possible.

---

#### GLM-4.7-Flash (30B-A3B) — under-evaluated local agentic contender
106B total, 12B active (NOT the same class as the other 30B-A3B models listed). SWE-Bench 59.2% [marktechpost.com]. Tau2-Bench (agentic multi-step tool invocation) 79.5% vs Qwen3-30B-A3B's 49% [grigio.org]. AIME 91.6, GPQA 75.2 [openrouter.ai]. MIT license. Runtime arch = `deepseek2`/MLA (`Glm4MoeLiteForCausalLM`). [DokiDex — decisions.md]

**Known risk:** OPEN llama.cpp issue **#21915** — GLM emits gibberish on turn 2+ with `q8_0` KV cache. Wired in llama-swap.yaml with KV-quant OFF on purpose. **#19009** (ignores `tool_choice` / loops in required+thinking mode) also open. The tool-call gate (`serving/test-toolcall.ps1`) has been hardened with T1b + T3 + `-GlmSampling` specifically for this. [DokiDex — decisions.md, llama-swap.yaml comment]

**DokiDex role:** Wired as `coder-candidate-glm` (commented out, needs eval-gate). Its Tau2-Bench and AIME edges suggest it may be better than Qwen3-Coder for agentic reasoning-in-loop tasks where non-thinking mode is a limitation. Worth eval-gating head-to-head, but #21915 must be resolved first.

---

#### GLM-4.6 / GLM-4.7 (full) — impractical at 32 GB
358B / 357B models. Even at UD-Q2_K_XL: ~135 GB. Realistic interactive throughput on 32 GB = ~5 t/s. Do not include in llama-swap rotation.

---

#### DeepSeek V4 Pro — strong cloud frontier, not locally runnable
1.6T MoE, ~864 GB weights. Needs 1 TB+ VRAM. Even experimental llama.cpp fork support (Discussion #22376) doesn't help on a workstation. API-only option if DeepSeek pricing becomes attractive.

---

#### DeepSeek R1-Distill-Qwen-32B — local reasoning chain specialist
Dense 32B distilled from R1 reasoning traces. Beats o1-mini on AIME/MATH [HuggingFace]. Runs at Q4 (~18 GB). BUT: SWE-MERA only 13.2% — very weak on real-world multi-file repo tasks [arxiv 2507.11059]. Effectively superseded by gpt-oss-20b for DokiDex's use case.

### 4.3 "Use X when…" decision table

| Model | Use when… |
|---|---|
| **Fable 5** | Task requires frontier-level multi-day autonomous engineering and you accept cloud API cost |
| **Claude Opus 4.8** | Best honest cloud coding agent at 2× lower cost than Fable 5; well-scoped tasks beyond local model capability |
| **GPT-5.5** | Terminal/shell-heavy agentic work, long-context retrieval at 512K-1M tokens, or omnimodal input |
| **gpt-oss-20b** | Local reasoning tasks, AIME-class math, bounded coding tasks; whenever you need thinking-mode responses without GPU swap cost |
| **gpt-oss-120b** | Same as 20b but for tasks where extra expert breadth matters (rare-domain questions, harder generation); accept slower throughput |
| **Qwen3-Coder-30B-A3B** | Local agentic coding with tool calls over large codebases (up to 256K context); repo-wide refactors; maximum local coding throughput |
| **Qwen3-VL-32B** | Any task requiring image/screenshot/chart understanding, GUI automation, OCR, or visual debugging |
| **GLM-4.7-Flash** | Local agentic coding where reasoning-in-loop matters (Tau2-Bench superior); eval-gate against coder-fast first |
| **GLM-4.6 / GLM-4.7 (full)** | Server-class hardware only; do NOT include in llama-swap rotation |
| **DeepSeek V4 Pro** | Cloud API alternative only; not locally deployable |
| **DeepSeek R1-Distill-Qwen-32B** | Pure math / competitive-programming reasoning as a specialized local model; superseded by gpt-oss-20b for most tasks |

---

## 5. What drives performance: the harness's role

### 5.1 Quantization choices

LLM inference is almost entirely **memory-bandwidth-bound** for token generation, not compute-bound. A smaller quant that fits more weights in VRAM cache → faster decoding, even at a few perplexity points of quality cost.

The UD-Q4_K_XL format on `coder-fast` (importance-matrix calibrated, slightly above Q4_K_M quality) is correctly chosen. The MXFP4 format on gpt-oss models is the native training format and loads correctly via ggml-org GGUFs — but note that llama.cpp dequantizes MXFP4 during loading (no FP4 tensor-core acceleration path in llama.cpp currently; that's a TensorRT-LLM feature). A Q4_K_XL repack of gpt-oss-120b may decode faster due to bandwidth optimization and is worth a one-time bench comparison. [Confirmed — arxiv 2601.14277, insiderllm.com]

### 5.2 KV-cache quantization: when to use it and when not to

The KV cache grows linearly with context × layers × head dimensions. Quantizing the KV cache is the primary lever for extending context without OOMing.

**Current DokiDex config:** [DokiDex — llama-swap.yaml]
- `coder-fast` / `coder-big`: `--cache-type-k q8_0 --cache-type-v q8_0` — correct, safe
- `fast-candidate-gptoss20b`: no KV quantization (F16) — **intentional**, documented as "q8_0 KV hurts gpt-oss" [DokiDex — llama-swap.yaml comment]
- `vision`: `--cache-type-k q8_0 --cache-type-v q8_0` — reasonable for 32K single-turn vision context

**Critical warning from official llama.cpp docs** [Confirmed — llama.cpp/blob/master/docs/function-calling.md]: *"Beware of extreme KV quantizations (e.g. `-ctk q4_0`), they can substantially degrade the model's tool calling performance."* Do not go below q8_0 on any tool-calling model.

**Flash Attention is a hard prerequisite** for KV quantization to be beneficial. Without `--flash-attn`, llama.cpp must dequantize the cache for every attention step, potentially slower than no quantization at all. All DokiDex configs correctly include `--flash-attn on`. [DokiDex — llama-swap.yaml]

**One remaining opportunity:** `vision` at 32K single-turn context could use `--cache-type-k q4_0` (single-turn vision describe is not tool-call-sensitive) to free ~1 GB VRAM headroom. Low priority — probably not needed.

### 5.3 Context length and prefill speed

Flash Attention converts attention from O(N²) memory-bandwidth to fused tiled kernels: 1.3–2× faster prefill, critical at 32K+ contexts. [Confirmed — Medium: 5 llama.cpp parameters]

RTX 5090 prefill speed drops from ~6,600 t/s at 4K context to ~1,450–3,000 t/s at 65K for 30–32B models. Decode speed is relatively stable vs context until KV overflows VRAM. [Confirmed — hardware-corner.net]

The current `ctx-size 131072` on `coder-fast` and `coder-big` is aggressive but intentional for long code sessions. `coder-fast-lite` at 65536 is the correct coexistence mode when FIM (~6.6 GB) is loaded alongside. [DokiDex — llama-swap.yaml]

### 5.4 Sampling parameters: the highest-return zero-weight change

Default llama.cpp sampling (temp 0.8, top-p 0.95, no min-p) is suboptimal for tool-calling and code generation. Mode-specific enforcement via llama-swap's `setParams` is the highest-leverage zero-code harness tuning path.

**Confirmed recommended values by task** [Confirmed — smcleod.net]:

| Mode | Temperature | Top-P | Min-P | Notes |
|---|---|---|---|---|
| Tool-calling / structured | 0.0–0.1 | 0.9 | 0.1 | Format drift rises fast above 0.3 |
| Coding (precision) | 0.1–0.4 | 0.9 | 0.05–0.1 | Variable names need repetition |
| Chat / factual | 0.3–0.6 | 0.9 | 0.1–0.15 | — |
| Reasoning models (gpt-oss-20b) | 0.0–0.3 | 0.9 | 0.1 | Low temp on visible output; thinking tokens use model's own temp |

**Min-P vs Top-P:** Min-P (0.05–0.1) consistently outperforms top-p at equal diversity levels by adapting its filter to the model's confidence. [Confirmed — letsdatascience.com]

**Repeat penalty caution for code:** `repeat_penalty > 1.05` penalizes variable/function names that legitimately repeat, causing naming drift. For coding, keep at 1.0 (disabled) or 1.05 max. [DokiDex — llama-swap.yaml: `coder-candidate-glm` already uses `--repeat-penalty 1.0`]

**DokiDex today:** No `setParams` in any model entry — default llama.cpp sampling applies. This is the most immediate actionable gap.

### 5.5 Batch size / ubatch and prefill throughput

- `-b` (batch-size, default 2048): logical maximum tokens per prefill step.
- `-ub` (ubatch-size, default 512): physical micro-batch sent to GPU per step.
- For CPU+GPU MoE offload (`coder-big`), **default 512 ubatch is too small**. The MoE offload guide recommends `-b 4096 -ub 4096` to let the GPU-offload threshold (`GGML_OP_OFFLOAD_MIN_BATCH`, default 32) kick in properly. [Confirmed — HuggingFace MoE offload guide, promptsicle.com]

> **DokiDex:** `coder-big` currently has `-b 2048 -ub 2048`. [DokiDex — llama-swap.yaml] Given it offloads 22 MoE layers to CPU, bumping to `-b 4096 -ub 4096` could meaningfully improve prefill throughput on long prompts. Add `GGML_OP_OFFLOAD_MIN_BATCH=256` as an env var to ensure CPU-assigned weights batch efficiently.

### 5.6 MoE expert CPU offloading (coder-big / gpt-oss-120b)

`--n-cpu-moe N` moves MoE expert tensors for the top N layers (from highest-numbered) to CPU RAM. Attention heads, dense FFN layers, and shared experts remain on GPU. [Confirmed — HuggingFace MoE offload guide]

**Tuning strategy:** Start `--n-cpu-moe` high, then *lower it* until VRAM hits ~90% capacity. Each step you lower offloads fewer experts to CPU → faster, but risks OOM. DokiDex currently uses `--n-cpu-moe 22` (of 36 layers). [DokiDex — llama-swap.yaml]

**Current throughput:** ~28–30 t/s based on community benchmarks at similar configs [hardware-corner.net]. Each reduction of 2 in `--n-cpu-moe` should yield ~3–5 t/s improvement. With 64 GB DDR5 (comfortably holding the expert pool), the bottleneck is DDR5 bandwidth rather than capacity.

**Thread tuning:** Set `--threads` to the number of physical CPU cores (not hyperthreads) — adding more threads beyond memory bandwidth saturation hurts due to cache thrashing. With DDR5 dual-channel, 4–8 threads typically saturate it. [Confirmed — HuggingFace MoE offload guide]

### 5.7 Prefix caching and KV reuse

`--cache-reuse 256` detects when an incoming prompt shares ≥256 tokens with a recently-processed slot, shifts the cached KV slice forward, and only prefills the new portion. On repeated calls with a 2,000-token system prompt: first-token latency drops from ~400 ms to <50 ms (43 tokens evaluated → 1 token evaluated on cache hit). [Confirmed — llama.cpp Discussion #13606]

**Cache invalidation conditions:** A single space difference in the system prompt invalidates the cache. Any dynamic content (timestamps, session IDs, per-request random values) injected into the system prompt before the conversation history kills the cache hit.

> **DokiDex:** `--cache-reuse 256` is correctly set on `coder-fast`, `coder-fast-lite`, and `coder-big`. [DokiDex] The C# Chat loop must ensure byte-stable system prompts across turns. Tool schema definitions in the system prompt should be frozen at startup, not regenerated per request.

### 5.8 Tool-call format and gpt-oss reasoning_content

**`--jinja` flag:** Enables Jinja template processing using the chat template embedded in the GGUF. Required for Qwen3, gpt-oss, and Qwen3-VL models. Without it, llama.cpp uses a generic template that breaks Qwen3's XML-based tool-call format. All DokiDex model configs correctly include `--jinja`. [DokiDex — llama-swap.yaml] [Confirmed — llama.cpp function-calling docs]

**gpt-oss `reasoning_content` handling:** gpt-oss models put chain-of-thought in `reasoning_content` and visible output in `content`. **The C# loop must not pass `reasoning_content` back to the model as part of the conversation history** — it should be stripped or stored separately. Passing it back poisons the context (the model sees its own thinking as user-visible text). [DokiDex — llama-swap.yaml comment: "give it a normal max_tokens or the visible content can come back empty"]

### 5.9 Context compaction for long agentic sessions

Past ~70% of ctx-size, models exhibit "context drift" — degraded reasoning from attention dilution across long histories. [Confirmed — zylos.ai] For `coder-fast` at 131K, the threshold is ~91K tokens.

**Strategies in order of quality preservation:**
1. Sliding window: drop oldest turns, keep system prompt + recent N turns. Simple, zero latency.
2. Hierarchical summarization: LLM summarizes dropped turns into a compact memory block. Higher quality, 1–2 extra LLM calls.
3. Structured memory: extract facts into key-value store, inject as compacted context. Best for multi-session agents.

The prefix cache means system prompt + tool definitions are "free" on every call (sub-50 ms prefill after the first hit). The cost is proportional to *new tokens since the last cache hit*.

### 5.10 GPU/LLM mode switching

DokiDex runs ONE model at a time on the RTX 5090 (LLM vs media = mutually exclusive). llama-swap's TTL controls idle unloading:

- `coder-fast`: no TTL (or `ttl: 0`) — keep hot as the daily driver. [DokiDex — llama-swap.yaml: no ttl set = globalTTL behavior]
- `vision`, `vision-8b`, `fast-candidate-gptoss20b`: should use `ttl: 30` — free VRAM promptly when idle.
- For the GPU-mode flip coordinator: send a DELETE/unload signal to llama-swap **before** starting SwarmUI, not rely on TTL. TTL is a safety net, not the trigger. [DokiDex — decisions.md: GPU-flip coordinator identified as must-prototype]

---

## 6. DokiDex prioritized improvement list

Each action is tagged: **FEASIBLE** (implementable now, no new installs), **GATED** (needs optional install or upstream fix), or **UPSTREAM-BLOCKED** (waiting on llama.cpp / llama-swap PR to land).

---

### Tier 1: Immediate, zero-risk, high-impact

**[1] Add per-call-mode sampling parameters via llama-swap `setParams` aliases**
Tag: FEASIBLE
File: `serving/llama-swap.yaml`

Add a `coder-fast:tools` alias that enforces `temperature: 0.1` + `min_p: 0.1` + `stripParams` (blocks client override). The main `coder-fast` entry uses `temperature: 0.6` for chat turns. For `fast-candidate-gptoss20b`, enforce `temperature: 0.0` on reasoning calls. No C# changes required — the C# loop requests model `coder-fast:tools` during structured extraction passes and `coder-fast` for chat. llama-swap serves both aliases from the same loaded process.

```yaml
# Example (add to llama-swap.yaml, same cmd as coder-fast):
"coder-fast:tools":
  cmd: |
    ... (same as coder-fast)
  setParams:
    temperature: 0.1
    top_p: 0.9
    min_p: 0.1
  stripParams: "temperature,top_p,min_p"
```

Expected impact: immediate reduction in tool-call format drift; likely eliminates the majority of malformed JSON tool calls.

---

**[2] Strip `reasoning_content` from gpt-oss turn history in the C# loop**
Tag: FEASIBLE
File: `control/Web/Chat.cs` or `LocalLlm.cs` (wherever turn history is assembled)

When a response from `fast-candidate-gptoss20b` (or any gpt-oss model) is appended to the conversation history, strip or omit `reasoning_content`. Store it separately for debugging/display if desired, but never inject it back into the prompt. Without this fix, the model sees its own internal thinking as user-visible text in subsequent turns, degrading coherence.

Expected impact: fixes multi-turn coherence for gpt-oss-20b, enabling it to handle agentic loops cleanly.

---

**[3] Add JSON-schema grammar enforcement on tool-argument extraction passes**
Tag: FEASIBLE
File: `control/Web/LocalLlm.cs` or wherever completion requests are built

Add `response_format: { "type": "json_schema", "json_schema": { ... } }` to completion requests during tool-argument extraction. The json_schema should match the expected tool argument structure. llama.cpp in b9616 supports this via GBNF auto-conversion. This eliminates tool-call parse failures by constraining sampling.

Add a 1–2 retry loop: when tool-call JSON fails to parse, re-call the model passing the parse error as a user message with a correction request. This is the single highest-ROI error-recovery pattern. [Confirmed — §8 above, SWE-bench structured error recovery +5–15 pts]

Expected impact: near-100% parse reliability on tool outputs; retry loop catches the remaining failures.

---

**[4] Freeze system prompt bytes across turns**
Tag: FEASIBLE
File: `control/Web/Chat.cs` or `ChatPrompt.cs`

Audit the system prompt assembly in `ChatPrompt.Build`: confirm no timestamps, session IDs, random values, or per-request dynamic content appears before the conversation history. The system prompt + tool schema block should be composed once at startup (or at session start) and reused byte-identical on every subsequent call. This maximizes `--cache-reuse 256` hit rate from the current unknown rate to 85%+.

Specific things to audit: any `DateTime.Now` or `Guid.NewGuid()` calls in the system prompt path; any per-request model-name injection into static blocks.

Expected impact: 43 tokens evaluated → 1 token evaluated per turn after the first call; sub-50 ms TTFT on all subsequent turns in a session.

---

**[5] Raise `coder-big` batch size for better prefill throughput**
Tag: FEASIBLE
File: `serving/llama-swap.yaml`

Change `coder-big` cmd from `-b 2048 -ub 2048` to `-b 4096 -ub 4096`. Add `GGML_OP_OFFLOAD_MIN_BATCH=256` as an environment variable in the llama-swap config for the `coder-big` entry (llama-swap supports env vars per model). This ensures the CPU-offloaded expert weights batch efficiently, improving prefill throughput on long prompts (the 131K context case).

Expected impact: meaningfully better prefill throughput on long prompts with `coder-big`. No quality change.

---

### Tier 2: Medium effort, high value

**[6] Implement context compaction at 70% ctx-size**
Tag: FEASIBLE
File: C# Chat loop

Track token count per turn. When the accumulated context approaches 70% of the model's ctx-size (~91K tokens for 131K context models), trigger a summarization pass: call the current model (or `fast-candidate-gptoss20b` for speed) to summarize the oldest N turns into a compact memory block, replace those turns in history with the summary. The system-prompt prefix still caches, so only the summary target tokens need prefilling.

Compress tool call results aggressively before appending: truncate file-read outputs to a configurable character limit (e.g., 4K chars), truncate grep results to top-N matches. This alone addresses the 76% read-token problem. [Confirmed — arxiv 2601.16746]

Expected impact: prevents context drift in sessions longer than ~30–50 turns with tool use; maintains task success rate in long agentic sessions.

---

**[7] Tune `--n-cpu-moe` on `coder-big` down from 22 toward 16–18**
Tag: FEASIBLE
File: `serving/llama-swap.yaml`

Lower `--n-cpu-moe` from 22 toward 16–18, checking VRAM usage after each 2-step reduction. Each step moves 2 more layers' expert weights from CPU to GPU. The target is the lowest value that keeps VRAM < ~30 GB. With 32 GB VRAM and `gpt-oss-120b-mxfp4` weights, there is likely headroom to bring more experts on-GPU.

Measure decode t/s before and after. Expected: +3–5 t/s per 2-step reduction.

---

**[8] Add `remaining_budget` signal to system prompt**
Tag: FEASIBLE
File: C# Chat loop + `ChatPrompt.Build`

Add a `<context_budget remaining="N">` block to the system prompt that updates each turn with remaining token budget. Instruction: "prefer targeted queries over broad searches when budget drops below 20%". This is a pure harness change (zero model weight changes) that causes the model to self-regulate expensive tool calls as context fills.

---

**[9] Add tool result truncation at injection time**
Tag: FEASIBLE
File: C# ChatTools loop (wherever tool results are appended to history)

Before appending any tool result to conversation history, truncate to a configurable per-tool character limit:
- `search_library`: top 5 results only, no raw embeddings
- `code_search`: top 3 results with snippets, max 500 chars per snippet
- `web_search`: top 3 results with summaries, max 800 chars per result

This is the cheapest and most impactful context management change — tool results are 76% of token consumption. [Confirmed — arxiv 2601.16746]

---

**[10] Eval-gate GLM-4.7-Flash via `evals/run-suite.ps1` before considering for coder-fast slot**
Tag: FEASIBLE (pre-work for candidate evaluation)

GLM-4.7-Flash is already wired as `coder-candidate-glm` in llama-swap.yaml (commented out). Before any swap consideration:
1. Resolve llama.cpp issue **#21915** (q8_0 KV gibberish on turn 2+) — monitor the issue; do not proceed until confirmed fixed.
2. Uncomment `coder-candidate-glm`, run `evals/run-suite.ps1 -Model coder-candidate-glm`.
3. Gate criterion: ≥91% on the golden gate (current coder-fast cleared this; gpt-oss-20b cleared it at 14/15 = 93%).
4. Run `serving/test-toolcall.ps1 -GlmSampling` (already hardened with T1b + T3 for this model).

The Tau2-Bench 79.5% and AIME 91.6 results suggest it may outperform Qwen3-Coder on reasoning-in-loop tasks. This is a bake-off, not a guaranteed swap.

---

### Tier 3: Higher effort, upstream-dependent

**[11] Enable MTP speculative decoding on `coder-fast`**
Tag: GATED on llama.cpp PR #22673 landing in the build

Qwen3-Coder-30B-A3B includes MTP heads. PR #22673 adds MTP speculative decoding support to mainline llama.cpp. Check whether b9616 includes #22673:

```powershell
# Check if build includes MTP support:
serving\llama.cpp\llama-server.exe --help | Select-String "draft"
```

If MTP is available, add `--draft-max 8` to the `coder-fast` cmd. Expected: 2–3× decode speedup on code completions (80%+ acceptance rates for code tasks). This is the single highest-impact performance change available for `coder-fast`.

If #22673 is not in b9616, set a reminder to test after the next llama.cpp build upgrade.

---

**[12] Add `fast-candidate-gptoss20b` to the full crush eval suite**
Tag: FEASIBLE (but needs planning for the multi-model eval)

gpt-oss-20b passed the tool-call gate (4/4) and cleared 14/15 on the golden gate, but the ≥91% golden-gate half is measured only on partial results. [DokiDex — decisions.md] Run `evals/run-suite.ps1 -Model fast-candidate-gptoss20b` for the full 15-task suite to confirm the ≥91% bar. If it clears, promote it to a documented on-demand reasoning tier.

The high-reasoning-budget mode (set via `reasoning_budget` or extended `max_tokens` on the thinking block) is the key lever — ensure the eval suite exercises this path, not just default generation.

---

**[13] TTL tuning for opportunistic model slots**
Tag: FEASIBLE
File: `serving/llama-swap.yaml`

Add `ttl: 30` to `vision`, `vision-8b`, and `fast-candidate-gptoss20b` entries. These models are infrequently needed and should free VRAM promptly after use. The `coder-fast` daily driver should have no TTL (or `ttl: 0`) to stay hot. This reduces unnecessary cold-swap latency on mode switches.

---

**[14] Investigate GGUF Q4_K_XL repack for gpt-oss-120b**
Tag: GATED on repack availability

The current `gpt-oss-120b-mxfp4` GGUFs are dequantized by llama.cpp during loading (no FP4 tensor-core path in llama.cpp). A Q4_K_XL repack (with importance-matrix calibration, similar to `coder-fast`'s UD-Q4_K_XL format) may offer better decode throughput due to bandwidth optimization. Check the ggml-org and unsloth repos for such a repack; if available, bench it head-to-head against the current MXFP4 files for t/s on `coder-big`.

---

### Summary: actions sorted by impact/effort

| Priority | Action | Where | Tag | Expected impact |
|---|---|---|---|---|
| 1 | Per-mode sampling via `setParams` aliases | llama-swap.yaml | FEASIBLE | Tool-call reliability, immediate |
| 2 | Strip `reasoning_content` from gpt-oss history | C# loop | FEASIBLE | Multi-turn coherence for gpt-oss-20b |
| 3 | JSON-schema grammar + retry loop on tool-arg extraction | LocalLlm.cs | FEASIBLE | Near-100% parse reliability |
| 4 | Freeze system prompt bytes (no dynamic content) | ChatPrompt.Build | FEASIBLE | 85%+ cache hit rate, sub-50ms TTFT |
| 5 | Raise `coder-big` to `-b 4096 -ub 4096` + OFFLOAD_MIN_BATCH | llama-swap.yaml | FEASIBLE | Better prefill on long prompts |
| 6 | Context compaction at 70% ctx-size | C# Chat loop | FEASIBLE | Prevents context drift in long sessions |
| 7 | Lower `--n-cpu-moe` on coder-big (22 → 16–18) | llama-swap.yaml | FEASIBLE | +3–5 t/s per 2-step reduction |
| 8 | `remaining_budget` signal in system prompt | ChatPrompt.Build | FEASIBLE | Self-regulating context usage |
| 9 | Tool result truncation at injection time | ChatTools loop | FEASIBLE | 76% read-token reduction |
| 10 | GLM-4.7-Flash eval-gate (pending #21915 fix) | evals/ + llama-swap | GATED (#21915) | New coder-fast candidate |
| 11 | MTP speculative decoding on `coder-fast` | llama-swap.yaml | GATED (PR #22673) | 2–3× decode speedup |
| 12 | Full crush eval suite for gpt-oss-20b | evals/ | FEASIBLE | Confirm ≥91% golden gate |
| 13 | TTL tuning for opportunistic model slots | llama-swap.yaml | FEASIBLE | Faster GPU mode switches |
| 14 | GGUF Q4_K_XL repack bench for gpt-oss-120b | llama-swap.yaml | GATED (repack availability) | Potential decode throughput gain |

---

## Sources

**Harness architecture:**
- [Dive into Claude Code (arxiv 2604.14228)](https://arxiv.org/html/2604.14228v1)
- [Building AI Coding Agents for the Terminal (arxiv 2603.05344)](https://arxiv.org/html/2603.05344v1)
- [Don't Break the Cache (arxiv 2601.06007)](https://arxiv.org/html/2601.06007v1)
- [Active Context Compression (arxiv 2601.07190)](https://arxiv.org/html/2601.07190v1)
- [Speculative Tool Calls (arxiv 2512.15834)](https://arxiv.org/pdf/2512.15834)
- [SWE-Pruner (arxiv 2601.16746)](https://arxiv.org/pdf/2601.16746)
- [Budget-Aware Tool-Use (arxiv 2511.17006)](https://arxiv.org/pdf/2511.17006)
- [Latency-Aware Parallel Multi-Agent Scheduling (arxiv 2601.10560)](https://arxiv.org/pdf/2601.10560)
- [Agent Scaffolding Beats Model Upgrades — Particula](https://particula.tech/blog/agent-scaffolding-beats-model-upgrades-swe-bench)
- [SWE-bench Scaffolding Analysis — DigitalApplied](https://www.digitalapplied.com/blog/swe-bench-verified-june-2026-benchmark-vs-scaffolding-analysis)
- [Harness Engineering for AI Coding Agents — Augment Code](https://www.augmentcode.com/guides/harness-engineering-ai-coding-agents)
- [llama.cpp Grammar & Structured Output — DeepWiki](https://deepwiki.com/ggml-org/llama.cpp/8.1-grammar-and-structured-output)
- [KV cache reuse tutorial (llama.cpp Discussion #13606)](https://github.com/ggml-org/llama.cpp/discussions/13606)
- [llama.cpp function-calling docs](https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md)
- [LLM Sampling Parameters Guide — smcleod.net](https://smcleod.net/2025/04/llm-sampling-parameters-guide/)
- [Temperature/Top-P/Min-P explained — letsdatascience.com](https://letsdatascience.com/blog/llm-sampling-temperature-top-k-top-p-and-min-p-explained)
- [llama-swap docs/configuration.md](https://github.com/mostlygeek/llama-swap/blob/main/docs/configuration.md)
- [Context compression strategies — Zylos AI](https://zylos.ai/research/2026-02-28-ai-agent-context-compression-strategies/)
- [Aider Architecture — Simran Chawla](https://simranchawla.com/understanding-ai-coding-agents-through-aiders-architecture/)

**Model creation:**
- [Qwen3 Technical Report (arxiv 2505.09388)](https://arxiv.org/abs/2505.09388)
- [Qwen3-Coder blog post — qwenlm.github.io](https://qwenlm.github.io/blog/qwen3-coder/)
- [DeepSeek-V3 Technical Report (arxiv 2412.19437)](https://arxiv.org/pdf/2412.19437)
- [DeepSeek-R1 paper (arxiv 2501.12948)](https://arxiv.org/pdf/2501.12948)
- [gpt-oss Model Card (arxiv 2508.10925)](https://arxiv.org/pdf/2508.10925)

**Inference efficiency:**
- [Which Quantization Should I Use? (arxiv 2601.14277)](https://arxiv.org/html/2601.14277v1)
- [NVIDIA NVFP4 Technical Blog](https://developer.nvidia.com/blog/introducing-nvfp4-for-efficient-and-accurate-low-precision-inference/)
- [Private LLM Inference on Blackwell (arxiv 2601.09527)](https://arxiv.org/html/2601.09527v1)
- [HuggingFace MoE offload guide (Doctor-Shotgun)](https://huggingface.co/blog/Doctor-Shotgun/llamacpp-moe-offload-guide)
- [RTX 5090 LLM benchmarks — hardware-corner.net](https://www.hardware-corner.net/rtx-5090-llm-benchmarks/)
- [gpt-oss MoE offloading — hardware-corner.net](https://www.hardware-corner.net/gpt-oss-offloading-moe-layers/)
- [Speculative decoding 2026 — sesamedisk.com](https://sesamedisk.com/speculative-decoding-llm-inference-speedup/)
- [MTP and llama.cpp with Qwen3 — dredyson.com](https://dredyson.com/the-hidden-truth-about-mtp-and-llama-cpp-with-qwen3-6-27b-why-speculative-decoding-will-reshape-local-ai-inference-in-2025-and-beyond-a-complete-forward-looking-analysis-for-developers/)
- [Boosting llama-server throughput — Promptsicle](https://promptsicle.com/tips/boosting-llama-server-performance-with-batch-settings/)
- [Choosing a GGUF model (IQ variants) — Kaitchup](https://kaitchup.substack.com/p/choosing-a-gguf-model-k-quants-i)
- [gpt-oss llama.cpp launch guide (Discussion #15396)](https://github.com/ggml-org/llama.cpp/discussions/15396)
- [Steering LLM Thinking with Budget Guidance (arxiv 2506.13752)](https://arxiv.org/pdf/2506.13752)
- [Nixie GPU Temporal Multiplexing (arxiv 2601.11743)](https://arxiv.org/pdf/2601.11743)

**Per-model benchmarks:**
- [LLM Leaderboard 2026 — Vellum](https://www.vellum.ai/llm-leaderboard)
- [Claude Fable 5 Benchmarks — Vellum](https://www.vellum.ai/blog/claude-fable-5-and-mythos-5-benchmarks-explained)
- [Claude Opus 4.8 Benchmarks — OpenRouter](https://openrouter.ai/anthropic/claude-opus-4.8/benchmarks)
- [GPT-5.5 Benchmarks — BenchLM.ai](https://benchlm.ai/models/gpt-5-5)
- [GPT-OSS 120B Benchmarks — BenchLM.ai](https://benchlm.ai/models/gpt-oss-120b)
- [Qwen3-Coder-30B-A3B — Hugging Face](https://huggingface.co/Qwen/Qwen3-Coder-30B-A3B-Instruct)
- [GLM-4.7-Flash — MarkTechPost](https://www.marktechpost.com/2026/01/20/zhipu-ai-releases-glm-4-7-flash-a-30b-a3b-moe-model-for-efficient-local-coding-and-agents/)
- [GLM4.7-Flash agentic benchmarks — grigio.org](https://grigio.org/glm4-7-flash-the-new-local-llm-king-at-30b-a3b/)
- [SWE-MERA Benchmark (arxiv 2507.11059)](https://arxiv.org/pdf/2507.11059)
- [DeepSeek V4 VRAM Requirements — knightli.com](https://knightli.com/en/2026/05/01/deepseek-v4-local-vram-quantization-table/)
