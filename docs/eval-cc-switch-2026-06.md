# cc-switch (farion1231/cc-switch) - evaluation for DokiDex

_Multi-agent ultracode eval, 2026-06-20. 45 agents, source-level + adversarial verification._

**Recommendation:** Use-an-alternative (native Claude Code config). Skip cc-switch.

**One-line:** Skip for DokiDex: your local stack already speaks Anthropic natively, so the one capability cc-switch would add (an Anthropic<->OpenAI proxy) is redundant, and adopting it would add a plaintext-key, unsigned-installer, auto-updating 7-tool app to a security-conscious single-user box for ~zero functional gain.

---

## Verdict

**Use-an-alternative / Skip.** For DokiDex specifically, cc-switch solves a problem you do not have and brings trust costs you specifically care about. Recommendation: **point Claude Code at DokiDex with native config; do not adopt cc-switch.** If you ever need rule-based model routing, reach for the single-purpose `claude-code-router`, not a 7-tool GUI.

---

## What cc-switch is

A cross-platform Tauri 2 desktop GUI (Rust + React, MIT, single maintainer "farion1231 / Jason Young") that manages API-provider profiles for **seven** AI coding tools (Claude Code, Claude Desktop, Codex, Gemini CLI, OpenCode, OpenClaw, Hermes). One-click/tray provider switching, ~50 built-in relay presets, unified MCP/skills/prompts management, a usage/cost dashboard, and an **optional built-in local proxy** that can translate between Anthropic and OpenAI formats with failover. Current release v3.16.3 (2026-06-14). Repo stats (105K stars / 1.5K issues) are real, independently re-confirmed.

Its three headline value props — (a) switching across *many* tools, (b) *many* relay presets, (c) an Anthropic↔OpenAI *translation proxy* — are all breadth features. You have **one** local backend, **two** tools, want **no** third-party relays, and (see below) need **no** translation. The fit is structurally poor.

---

## Security & supply-chain trust (weighted heaviest)

| Area | Finding | Status |
|---|---|---|
| **Key storage** | API keys/tokens stored as **plaintext JSON** in unencrypted SQLite at `C:\Users\<you>\.cc-switch\cc-switch.db` (`providers.settings_config`/`meta` TEXT columns). No OS keychain, no app encryption. WebDAV sync password also plaintext. | **Confirmed** (source verified at commit `4555563`; `rusqlite` `bundled`, not `sqlcipher`; DAO writes `serde_json::to_string` straight to TEXT) |
| **Telemetry** | **No analytics/telemetry/crash SDK** (no Sentry/PostHog/etc., checked `Cargo.lock` incl. transitive deps). `reqwest` only for explicit features. Panic info goes to a local `crash.log`, not the network. | **Confirmed** |
| **Phones home** | On **every launch** it auto-checks for updates via the Tauri updater, fetching `github.com/.../releases/latest/download/latest.json`. Check-only (install needs a click); no PII beyond a normal GitHub HTTPS hit; **no in-code opt-out found** (block at firewall for zero egress). | **Confirmed** |
| **Windows installer signing** | MSI/portable EXE are **NOT Authenticode-signed** — *proven by downloading v3.16.3 and running `Get-AuthenticodeSignature` → NotSigned*. Only macOS is Apple-signed/notarized/stapled. Expect SmartScreen/Defender friction — exactly your recent pain point. | **Confirmed** |
| **Provenance** | Built in transparent GitHub Actions (`pnpm tauri build`, frozen lockfile), published by `github-actions[bot]`. **No SHA256SUMS manifest**, but GitHub's release API exposes a per-asset `sha256` digest you can recompute; updater `.sig` is minisign, verifiable against the pubkey embedded in `tauri.conf.json`. | **Confirmed** |
| **Bundled curl\|iex installer** | Its in-app "install tool" buttons can download+run upstream vendor scripts; the **Windows Hermes path is literally `irm …NousResearch…install.ps1 \| iex`** — the ClickFix shape you avoid. User-triggered, not at startup; never fires if used as a pure switcher. **Avoid those buttons.** | **Confirmed** |
| **Relay-balance calls** | The feature that sends your key only fires for a hardcoded allowlist (DeepSeek/StepFun/SiliconFlow/OpenRouter/Novita) and is a **no-op for a localhost endpoint**. | **Confirmed** |
| **Governance** | Single maintainer, MIT, very active, real `SECURITY.md`, **no published advisories**. An auto-updating desktop app from one maintainer + the minisign signing key in a GitHub secret = a standing supply-chain dependency that auto-flows to your machine. | **Confirmed** |

### What it writes to `~/.claude` (correction to the recon)

- It **does** write Claude Code's live config on switch/takeover: `~/.claude/settings.json` and `~/.claude.json`, atomic temp+rename, with rotated auto-backups (default 10, under `~/.cc-switch/backups/`). **Confirmed.**
- ⚠️ **NOT a merge of only the `env` block.** The recon claim that it "rewrites only the `env` block (merge, not overwrite)" is **REFUTED** by source: switching writes the selected provider's **entire stored `settings_config` object** to `settings.json` (whole-file atomic replace, minus 4 internal fields). Other top-level keys survive only because they were captured into that provider's record at import/backfill time. **Net for you:** treat it as a whole-file writer of your Claude config, and back up `~/.claude` yourself before first use.
- ⚠️ The scary-sounding **"signature bypass"** utility is **NOT** what the recon feared. The claim that it "defeats Claude Code's login/config-integrity check" is **REFUTED** by source. It is two unrelated, benign features: (1) an onboarding-skip toggle that writes `hasCompletedOnboarding=true` (a field Claude Code itself reads), and (2) "thinking-signature rectification" in the *proxy* that strips incompatible `thinking`-block signatures for relay compatibility. Neither is an OS code-signing bypass or a config-integrity defeat. Good news, but it shows the README wording is genuinely misleading.

---

## Does it help point Claude Code at DokiDex? (The core question)

**Two layers:**

**1. cc-switch *can* do it without a separate proxy — confirmed at source/UI level.** A Claude provider has an "API Format" selector (`Anthropic Messages` / `OpenAI Chat Completions` / `OpenAI Responses API`); choosing OpenAI Chat + enabling Claude takeover runs its built-in proxy (`127.0.0.1:15721`, local-only by default) which executes real `anthropic_to_openai`/`openai_to_anthropic` translation and forwards to your `base_url`. Arbitrary/localhost base URLs are accepted (no host allowlist). So in principle cc-switch alone replaces claude-code-router/LiteLLM. **Confirmed** — *but* this was **NOT runtime-verified end-to-end** (not established), the proxy is CHECK-scoped to `claude/codex/gemini` buckets, and **there are open bugs on exactly your path**: *"proxy produces empty content messages causing 400 with llama.cpp"* (#2467) and *"PROXY_MANAGED literal sent as API key to openai_chat backend, causing 401"* (#1510), both confirmed OPEN.

**2. You almost certainly don't need any of that.** This corrects the premise in your own briefing. **llama.cpp's server natively implements `POST /v1/messages` (the Anthropic Messages API)** — confirmed in source (`post_anthropic_messages`), README, and a dedicated compat test; and **llama-swap explicitly proxies `/v1/messages`** — confirmed. Your pinned llama.cpp build (b9616, 2026-06-12) post-dates the `/v1/messages` merge (2025-11-28), and your `llama-swap.yaml` already passes `--jinja` (required for tool use). So Claude Code → `http://127.0.0.1:8080/v1/messages` works with **pure native config**, no translation proxy at all. (Caveat: upstream makes "no strong claims of compatibility," so robustness is empirical — but the path exists in your exact build.)

**z.ai / GLM path:** cc-switch ships native `Zhipu GLM` / `Zhipu GLM en` Claude presets pointing at Anthropic-compatible endpoints (`/api/anthropic`), so that path is a pure base-URL+key swap with **no proxy** — **confirmed**, and well-maintained (GLM 5.1). But GLM is a cloud relay; irrelevant to a local-first setup unless you specifically want a cheap cloud coder alongside the local one. Note: you don't need cc-switch for this either — it's two env vars in a settings file.

---

## Comparison: native config vs. cc-switch vs. routers

| Option | What it gives you | Trust surface | Fit for DokiDex |
|---|---|---|---|
| **Native Claude Code config** (`settings.json` `env` + `--settings` file/inline + `--setting-sources`) pointed at llama.cpp's `/v1/messages` | One-flag per-session switching (`claude --settings dokidex.json` vs `claude` for cloud); `ANTHROPIC_BASE_URL` is the documented routing var. **All confirmed in Anthropic docs.** | **Zero added** — no binary, no key DB, no daemon | **Best.** Covers 100% of your need. Two PowerShell functions = the whole switching UX. |
| **claude-code-router** (MIT, 35K★, TS, active — confirmed) | Rule-based routing (background/think/longContext), per-provider transformers, OpenAI-only backends | Small, npm-auditable, single-purpose; no unsigned MSI, no relay presets | **Only if** you later want per-task model routing native env vars can't express |
| **LiteLLM** (50K★, but **SPDX `NOASSERTION`** not clean MIT — confirmed; Python) | Heavyweight multi-team gateway, Anthropic `/v1/messages` translation | Large; ambiguous license matters for your provenance diligence | **Avoid** — maximal dependency, ambiguous license, redundant translation |
| **cc-switch** | GUI/tray switch across 7 tools + relays + translating proxy | **Largest** — plaintext key DB, unsigned auto-updating installer, 7-tool footprint, curl\|iex buttons | **Poor** — breadth you won't use; security costs you specifically dislike |
| **y-router** | (Anthropic→OpenAI shim) | — | **Skip** — **archived 2026-01-11** (confirmed) + curl\|bash install |

---

## Bottom line

For a single-user, security-conscious, local-first box where **the backend already speaks Anthropic**, cc-switch is a net increase in attack/trust surface (plaintext keys, unsigned auto-updating installer, whole-file Claude-config writes, bundled curl|iex installers) to automate a job that **two env vars in a settings file already do**. **Skip it.**

**Do this instead** (smallest trust surface):
1. Create `~/.claude/dokidex.settings.json` with an `env` block: `ANTHROPIC_BASE_URL=http://127.0.0.1:8080`, plus a **dummy non-empty `ANTHROPIC_AUTH_TOKEN`** (Claude Code requires a value; llama.cpp ignores it). Point the model env at your Qwen3-Coder alias.
2. Two PowerShell functions: `cc-local` = `claude --settings $HOME\.claude\dokidex.settings.json`; `cc-cloud` = `claude`.
3. Behavioral nuance: a non-first-party `ANTHROPIC_BASE_URL` disables MCP tool search by default — set `ENABLE_TOOL_SEARCH=true` if you use MCP tools through the local endpoint.
4. Reserve `claude-code-router` (npm, MIT, audit it first) **only** if you later want rule-based multi-model routing.

**If you adopt cc-switch anyway**, minimize exposure:
1. Use the **Windows Portable ZIP** (no MSI, no registry footprint), not the installer.
2. **Verify before running:** `Get-FileHash -Algorithm SHA256 <file>` and compare to the asset's `digest` from `https://api.github.com/repos/farion1231/cc-switch/releases/latest`. For v3.16.3: MSI = `ca5c8120b6d01aeb4d983edf75ad15b94ae7f2aacf061b59921c5f9f090c2776`, Portable zip = `0670fc91b02ea530ebb1db334e252d286ebfe3a5e50d0c88d33a426c2388e7a0` (both confirmed against the API). Optionally minisign-verify the `.sig` against the pubkey in `tauri.conf.json`. *(Note: the digest shares GitHub's trust root with the binary — it proves no in-transit tampering, not independent provenance.)*
3. Use a **dummy key** for the local DokiDex profile; keep your real cloud Anthropic key out of the plaintext DB if you can stay on Claude Code's own login. **Never enable WebDAV/cloud sync** (copies plaintext keys off-box).
4. Keep the proxy bound to `127.0.0.1`; **back up `~/.claude` and `~/.codex/config.toml` first** (whole-file writes; an open Codex bug #4254 — confirmed — can overwrite `config.toml`); **avoid the in-app "install tool" buttons** (curl|iex); pin a known-good version and block the startup update-check at the firewall for change control.

---

## Explicitly not established (treat as unproven)

- **Claude Code → cc-switch proxy → llama.cpp round trip** was not runtime-verified (and there are open llama.cpp-path bugs). Native `/v1/messages` likewise verified at the *mechanism* level, not runtime, against your build.
- The recon's "switch merges only the `env` block," "signature bypass defeats Claude Code's integrity check," "proxy port is *fixed* at 15721" (it's the configurable default), and "Gemini CLI hot-reloads per request" were all **REFUTED** by source — don't rely on them.
- "First run triggers SmartScreen/Defender" is a sound inference from the *proven* unsigned state, not a separately documented fact.
- Reputation/safety of the ~50 bundled third-party relay presets was not assessed (irrelevant if you ignore them).
