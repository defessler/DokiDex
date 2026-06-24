# DokiDex stack improvement plan (2026-06)

Synthesis of two 2026-06 efforts, framed on improving the stack's **efficiency** (VRAM, latency, tok/s,
GPU-mode-switch) and **effectiveness** (task success, model tiers, eval-gating):

- **Codebase audit** → [`audit-2026-06.md`](audit-2026-06.md) — 45 findings across architecture,
  PowerShell/serving, security, tests, efficiency.
- **AI harness + model research** → [`research-harness-models-2026-06.md`](research-harness-models-2026-06.md)
  — harness architecture, how the models are built, a per-model comparison, what drives performance + the
  harness's role; 14 prioritized actions.

**Key insight:** we can't change the model weights, so **the harness + serving config is the highest-
leverage lever** — the same 32GB models perform measurably better with the right `llama-swap.yaml` knobs
and chat-loop handling.

## 1. Security + correctness — the audit P1s (DONE this pass)

| # | Fix | File | Status |
|---|---|---|---|
| P1-1 | CSRF: deny no-Origin / foreign-Origin state-changing requests | `control/Web/LocalSecurityMiddleware.cs` | ✅ + tests |
| P1-2 | edit_image: scope-validate LLM source paths (no traversal/absolute/non-media) | `control/Web/ChatTools.cs` | ✅ + tests |
| P1-3 | Broken gated kinds: add `Audio`/`Engine` to `GenRequest` (InfiniteTalk/LatentSync/Speak) | `control/Services/GenArgs.cs` | ✅ + tests |
| P1-4 | CUDA env leak: scope `CUDA_VISIBLE_DEVICES=-1` to the embed child only | `serving/start-embed.ps1` | ✅ |
| P1-5 | Download integrity: SHA-256 verify before promoting `.part` (+ SAM atomic download) | `setup.ps1` | ✅ |
| P1-6 | `SwarmGen.TryHandle` (drives every gen): make testable + cover | `control/Web/SwarmGen.cs` | ✅ + tests |
| P1-7 | `LocalSecurityMiddleware`: regression tests | `control/DokiDex.Control.Tests/` | ✅ |

+24 new xUnit tests; full `doki test` green. The two exploitable holes (CSRF, edit_image arbitrary-file
read) are closed. Lower-severity **P2/P3 findings remain open** in the audit report.

## 2. Effectiveness + efficiency — research-backed (PROPOSED, next pass)

Highest-ROI harness/serving changes (full list + rationale in the research report), each tagged by feasibility:

1. **[FEASIBLE] Per-role sampling for tool calls** — add a `coder-fast:tools` alias / `setParams` in
   `serving/llama-swap.yaml` with a lower temperature; the default (~0.8) hurts tool-call reliability *now*.
   Zero C# change, immediate effectiveness win.
2. **[FEASIBLE] Strip `reasoning_content` from gpt-oss turn history** in the C# chat loop — fixes multi-turn
   coherence for the gpt-oss `reasoning` tier (`control/Web/LocalLlm.cs` / `Chat.cs`).
3. **[FEASIBLE] JSON-schema grammar + retry on tool-arg extraction** — enforce tool-call argument shape
   (llama.cpp GBNF grammar) + a 1–2 retry loop → fewer malformed tool calls.
4. **[FEASIBLE] Freeze system-prompt bytes** (no timestamps/IDs in the prefix) to maximize the
   `--cache-reuse 256` prefix-cache hit rate → lower latency + tokens.
5. **[FEASIBLE] `coder-big` prefill throughput** — raise `-b 4096 -ub 4096` (+ offload-min-batch) for
   better prefill on the CPU-offloaded 120B.
6. **[+ 9 more]** — KV-cache/context tuning, speculative/draft decoding, model-tier/role assignments,
   context compaction. See [`research-harness-models-2026-06.md`](research-harness-models-2026-06.md).

## 3. Strategic (high-effort, follow-up)

- **Split the God-files** (audit's dominant structural risk): `control/Web/StudioHost.cs` (1,296 lines /
  one 1,170-line `MapApi`) → per-area endpoint modules; `control/Web/wwwroot/index.html` (2,741-line SPA)
  → per-view modules. Do these opportunistically when next working in those areas.

## Sequencing
P1s are done. Next: the five **[FEASIBLE]** harness/serving wins (§2.1–2.5 — mostly config, high ROI,
low risk), each behind the eval gate (`evals/run-suite.ps1` ≥91% golden **and** zero tool-call flakes)
before promotion. Then the strategic God-file splits as those areas are touched.
