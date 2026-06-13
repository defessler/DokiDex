# Decision log

## 2026-06-12 — Phase 2: harness bake-off → **Crush** is the daily driver

8-run matrix (2 harnesses × 2 models × 2 golden tasks), headless via `evals/run-eval.ps1`, objective pass checks. Clean results after fixing a check bug (array `-notmatch` false-negative):

| Combo | t1 feature (+test) | t2 bugfix |
|---|---|---|
| Crush × coder-fast | ✅ 53.7s | ✅ 18.9s |
| OpenCode × coder-fast | ✅ 41.4s (1 no-op flake before it) | ✅ 12.4s |
| Crush × coder-big | ✅ 120.7s | ✅ 120.4s |
| OpenCode × coder-big | ✅ 103.1s | ✅ 67.7s |

**Both harnesses work well with both local models — 8/8 task success on clean runs.**

### Why Crush wins as default

1. **Zero flakes** in the matrix; OpenCode hit one no-op run (model answered without tool calls; opencode run ended silently after 3.3s). Small sample, but consistent with Crush's tighter prompting of open models.
2. **Windows-native Go binary** — no Node runtime, instant startup, no npm shim issues (OpenCode's `.cmd` shim breaks on `<`/`>` in prompts when invoked via cmd; we bypass with the native exe in the runner).
3. **Scoped permission model that fits our eval design**: per-workspace `crush.json` `permissions.allowed_tools` enables headless runs in sandboxes while the global config keeps interactive ask-mode.

**OpenCode stays installed as challenger** — it was consistently slightly *faster* on tasks, its explore-subagent behavior is genuinely good, and the eval suite can re-judge any time (`run-eval.ps1 -Harness opencode`).

### Permission findings (important)

- **Interactive** Crush prompts before tool use by default (no `allowed_tools` in our global config; `--yolo` exists to bypass — i.e., prompting is the default).
- **Headless `crush run` auto-executes tools** — measured: it deleted a file when asked, with NO allowlist configured. Same class of behavior in `opencode run`.
- **Rule: never point headless `run` at a repo you care about without committed git state.** Eval runs always use throwaway copies (runner enforces this).

### Harness invocation gotchas (encoded in run-eval.ps1)

- `crush run`/`opencode run` block forever on a never-closing stdin pipe → always redirect stdin from an empty file in automation.
- Pass prompts after a `--` terminator: `Start-Process` joins args unquoted, so `--reverse` inside a prompt parses as a flag otherwise.
- Use OpenCode's native exe (`node_modules\opencode-ai\bin\opencode.exe`), not the npm `.cmd` shim.

### Model observations (small-n, watch in Phase 3)

- coder-fast ≈ 2–5× faster wall-clock than coder-big on identical tasks; both solved everything here.
- coder-big's wins should show on harder/longer tasks; t1/t2 are too easy to separate them.

## 2026-06-12 — Phase 4: web search = keyless DuckDuckGo MCP

- **`duckduckgo-mcp-server` via `uvx`** (no API key, stdio, actively maintained). SearXNG (self-hosted, Docker) is the documented upgrade path if DDG result quality limits us — not needed yet.
- Wired as `mcp.websearch` in the Crush config. Kept it the **only** MCP server — open models lose tool-selection accuracy as the tool list grows (TDD §6.2).

## 2026-06-12 — Phase 5: FIM model = Qwen2.5-Coder-3B (not 7B)

- TDD suggested a 7B FIM model, but at 32GB VRAM the **3B Q8** (~3GB) is the better pick: it coexists with the agent model and FIM latency favors smaller. Quality at 3B for fill-in-the-middle is excellent (292 tok/s, correct completions).
- **Coexistence solved with `coder-fast-lite`** (coder-fast at 64k ctx, ~21GB) instead of a tinier FIM model: 64k is plenty of agent context, and 21GB + 6.6GB FIM = ~27.6GB fits with margin. Full-128k coder-fast (30GB) cannot run alongside FIM.
- FIM served on a **dedicated `:8012`** rather than a llama-swap group — keeps autocomplete always-on and independent of which agent model is swapped in.
