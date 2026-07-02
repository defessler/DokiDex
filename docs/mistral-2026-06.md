# Mistral AI → DokiDex deep-dive (2026-06-30)

> A `/smartplan` deep-dive into mistral.ai, rolled into the stack plan. Two parallel research passes (models +
> 32GB-runnability; Le Chat / Agents API / UX), each adversarially verified against primary sources.
>
> **Sourcing:** [Confirmed] = primary doc / model card / arXiv. [Vendor] = Mistral's own benchmark/marketing
> (directional only — **eval-gate before promoting**). [DokiDex] = verified against this repo.

---

## 1. The five decisions

| Slot | Decision | Model | Why |
|---|---|---|---|
| **A — coder / `doki code`** | **ADOPT (eval-gate)** | **Devstral-Small-2-24B-Instruct-2512** (Apache 2.0) | Purpose-built for the agentic Read/Grep/Edit/Write/Bash tool loop `doki code` runs (co-built with All Hands / OpenHands). SWE-bench Verified **68.0%** [Vendor/card] vs Qwen3-Coder-30B ~50.3% [Vendor]. Smaller VRAM (Q4 14.3 GB), 256K ctx, official-quality community GGUF. |
| **B — FIM `:8012`** | **UPGRADE if GGUF findable** | **Codestral 25.01** (MNPL, personal-use) | 95.3% FIM vs 22B-v0.1's 91.8% [Vendor]. 25.08 is enterprise-only. Mamba-Codestral: llama.cpp Mamba2 FIM **not** wired — skip. |
| **C — reasoning** | **EVAL-GATE** | **Magistral-Small-2507** (Apache 2.0, official GGUF) | AIME'24 70.7% pass@1 [arXiv 2506.10910]. Low-friction swap vs gpt-oss-20b; watch verbose thinking-mode latency. |
| **D — vision** | **SKIP Mistral, keep Qwen3-VL-32B** | — | No Mistral open vision model fits 32GB *and* beats a dedicated 32B (Pixtral-12B MMMU 52.5 ≪ Qwen3-VL; Pixtral-Large is MRL + 300GB). |
| **E — audio (NEW slot)** | **EVAL-GATE (future)** | **Voxtral-Small-24B-2507** / **Voxtral-Mini-3B** (Apache 2.0, GGUF) | New capability: voice→media round-trip + local dictation. Defer behind the chat-surface work. |

**The standout is Slot A.** Devstral-Small-2 is the same 24B class, Apache-2.0, *smaller* in VRAM, longer-context, and **explicitly trained for the exact tool-loop `doki code` implements**. The +18pp SWE-bench gap is vendor-reported across three sources — promising, but **gate it through `evals/run-suite.ps1` (≥91% golden) + `serving/test-toolcall.ps1` against the live `doki code` loop before making it the default.**

---

## 2. Models — verified facts + 32GB fit

### Adopt / eval-gate (open, fits 32GB)
| Model | License | Size | Best at | GGUF | Q4 / Q8 VRAM | Key metric |
|---|---|---|---|---|---|---|
| **Devstral-Small-2-24B-2512** | Apache 2.0 | 24B dense | agentic coding | `bartowski/…`, `unsloth/…` | 14.3 / 25.1 GB | SWE-bench **68.0%** [Vendor/card], 256K ctx |
| **Magistral-Small-2507** | Apache 2.0 | 24B dense | reasoning | `mistralai/Magistral-Small-2507-GGUF` (official) | 14.3 / 25.1 GB | AIME'24 **70.7%** [arXiv], 128K |
| **Codestral 25.01** | MNPL (personal) | 22B dense | FIM completion | check `mistralai/Codestral-2501` | ~13 GB Q4 | FIM **95.3%** [Vendor], 256K |
| **Codestral 22B v0.1** | MNPL (personal) | 22B dense | FIM completion | `QuantFactory/…` | ~13 GB Q4 | FIM 91.8% [Vendor] |
| **Voxtral-Small-24B-2507** | Apache 2.0 | 24B | audio understand/ASR | `bartowski/…` | ~14 GB Q4 | 30-min transcribe, voice tool-calls |
| **Voxtral-Mini-3B-2507** | Apache 2.0 | 3B | dictation/ASR | `bartowski`, `ggml-org` | ~2.5 GB | realtime <500ms |
| Mistral-Small-3.2-24B | Apache 2.0 | 24B | general+vision | community | ~14 GB Q4 | MMLU 84.5 [3rd-party xref] |
| Pixtral-12B-2409 | Apache 2.0 | 12B | vision | `bartowski/…` | ~8 GB | MMMU 52.5 (< Qwen3-VL) |

### Skip (too big / wrong license / no GGUF)
Devstral-2-123B (multi-GPU), Pixtral-Large-124B (MRL + 300GB), Mistral-Medium-3.5-128B (multi-GPU), Magistral-Medium (API-only), Codestral 25.08 (enterprise-only), Mamba-Codestral-7B (llama.cpp Mamba2 FIM unconfirmed), Mixtral 8x7B/8x22B (retired 2025-03), Mistral-Embed/OCR (API-only).

### Concurrent-VRAM reality [DokiDex constraint]
Devstral Q8 (25 GB) + FIM Codestral (13 GB) = **38 GB > 32 GB**. For coexist-with-FIM mode, run Devstral Q4 (14.3 GB) + Codestral Q3 (~11 GB) ≈ 25 GB; or keep them in separate llama-swap slots (the existing `coder-fast-lite` pattern). Reasoning (Magistral) swaps *in for* Devstral, not alongside.

---

## 3. UX / API patterns to fold in (ranked, tagged)

**[L]** feasible-locally · **[O]** gated-on-optional-install · **[C]** cloud-only (copy the pattern, not the dependency)

1. **[L] Text SEARCH/REPLACE edit blocks** — *triple-confirmed* (Aider + Cline/Roo + Mistral's own `mistral-vibe` CLI). Let the model emit `<<<<<<< SEARCH / ======= / >>>>>>> REPLACE` in its **content**; parse + apply via the existing `CodeEdit.ApplyEdit`. Beats JSON edit-args for open coder models. **Highest-value `doki code` refinement.**
2. **[L] Raw FIM, suffix-first** — Codestral order is `<s>[SUFFIX]{suffix}[PREFIX]{prefix}` with **no `[MIDDLE]` token**; Qwen uses `<|fim_prefix|>{p}<|fim_suffix|>{s}<|fim_middle|>`. Temp ~0.2. Fix/confirm the `:8012` server builds the right order per model.
3. **[L] GBNF grammar for structured output** — convert JSON-Schema → GBNF for llama.cpp (avoids Mistral's json-mode infinite-whitespace gotcha). Reinforces the Stage-1 tool-arg-reliability idea.
4. **[L] Persisted agents (versioned config) + stateful conversations (append/restart/branch)** — `doki code --agent <name>`; "restart-from-entry" = chat-level checkpoints (ChatStore already branches).
5. **[L] Per-tool read/write risk tagging + persisted "always allow"** — extends the `doki code` approval gate (read tools stop prompting once allowed).
6. **[L] Handoffs (planner→coder→reviewer)** — a primitive that swaps the llama-swap tier mid-task and carries conversation state across models (Devstral→Magistral→Devstral).
7. **[L/O] MCP client** — be an MCP client; ship local filesystem/git servers, remote ones optional.
8. **[L] Canvas / artifact pane** (web) · **[L] citations as reference-chunk spans** · **[L] Memories** (DokiDex has a memory store) · **[L] Projects** (thread+files+instructions scope).
9. **[O] Sandboxed code interpreter** (subprocess+rlimits / container) · **[O] Voxtral voice in, local TTS out**.
10. **[L] Image-gen as a chat tool** — DokiDex already wraps SwarmUI; mirror Le Chat's `image_generation`-tool shape returning an artifact.

**Cloud-only (substitutes exist):** `web_search_premium` (AFP/AP) → local SearXNG/MCP; hosted FLUX-Ultra → local FLUX.1-dev; Mistral OCR *model* → another OCR model, same →markdown→RAG pattern.

---

## 4. Load-bearing specs (verified verbatim)

**SEARCH/REPLACE** (from `mistralai/mistral-vibe` `search_replace.md`): SEARCH must match **exactly once** (error on 0 or >1 → forces a re-Read), match **exactly** incl. whitespace; multiple blocks **apply in order** (later see earlier results). *(DokiDex's `CodeEdit` adds a whitespace-flexible rung — strictly more forgiving for weak models; keep it.)*

**FIM**: `POST /v1/fim/completions {model, prompt, suffix, …}`; over llama.cpp build the raw prompt (suffix-first for Codestral, no `[MIDDLE]`). [Confirmed — HF Codestral-22B disc #5, llama.cpp #2818]

**Agents/Conversations/Chat split**: `/v1/chat/completions` stateless; `/v1/conversations` server-stored (append, `/restart`, branch, built-in tools live here); `/v1/agents` persistent `agent_id` + version aliases; `handoff_execution: server|client`. `tool_choice: auto|any|none`. [Confirmed — docs.mistral.ai]

**Note:** Le Chat rebranded **"Vibe"** (~2026-05); coding features under "Work" mode + the Devstral 2 **Vibe CLI** (Mistral's own Claude-Code-style terminal agent — direct prior art for `doki code`).

---

## 5. Roll-in to the DokiDex plan (prioritized)

1. **Eval-gate Devstral-Small-2-24B for `doki code`** — add a commented `coder-candidate-devstral` llama-swap entry; `doki code --model coder-candidate-devstral`; gate via test-toolcall + run-suite + a real `doki code` task before defaulting. *Biggest single win; the model is built for this exact loop.*
2. **Add the text SEARCH/REPLACE edit path to `CodeAgent`** — parse edit blocks from model content (not just JSON Edit args), apply via `CodeEdit`. Triple-confirmed; keep the JSON `Edit` tool too (dual path).
3. **Confirm/fix the `:8012` FIM token order per model** (suffix-first Codestral vs Qwen `<|fim_*|>`); optionally upgrade to Codestral 25.01 if a GGUF is findable (MNPL personal-use).
4. **GBNF grammar for tool-arg/structured output** (the Stage-1 §5.4 reliability idea, now reinforced).
5. **Eval-gate Magistral-Small-2507** vs gpt-oss-20b for the reasoning tier (official GGUF; watch thinking-mode latency).
6. **Future:** Voxtral voice slot (after the chat-surface design); handoffs primitive; MCP client; Canvas/citations/Projects in the web chat.

**Discipline:** every model number above is **vendor/paper-reported**, not third-party-replicated. Promote nothing past the eval gate (`evals/run-suite.ps1` ≥91% golden **and** zero tool-call flakes) on DokiDex's own workload.

---

## 6. Sources
Models: docs.mistral.ai/getting-started/models, mistral.ai/news/{devstral-2-vibe-cli, devstral-2507, codestral-2501, codestral-25-08, voxtral}, HF cards (Devstral-Small-2-24B-2512, Magistral-Small-2507-GGUF, Pixtral-{12B,Large}, Voxtral-Small-24B), arXiv 2506.10910 (Magistral). UX/API: docs.mistral.ai/{capabilities/function_calling, api/endpoint/fim, capabilities/structured-output, studio-api/agents, api/endpoint/ocr, capabilities/citations}, github.com/mistralai/{mistral-vibe, mistral-common}, docs.continue.dev/guides/set-up-codestral.
