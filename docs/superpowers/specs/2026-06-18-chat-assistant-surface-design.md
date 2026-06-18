# DokiGen Studio — Native Chat / Assistant Surface (Design for Approval)

**Status:** DESIGN — owner approval required before any code is written (DokiDex design-then-approve discipline).
**Scope:** A native chat/assistant view inside DokiGen Studio's embedded SPA, backed by new `/api/chat*` endpoints over the existing local LLM stack. Loopback-only, single-user, uncensored.
**Author seams verified against repo:** `control/Web/StudioHost.cs`, `control/Web/LocalLlm.cs`, `control/Web/LlmTiers.cs`, `control/Web/Vision.cs`, `control/Web/StudioHub.cs`, `control/Web/LocalSecurityMiddleware.cs`, `control/Web/wwwroot/index.html`, `control/Services/Control/{ServiceRegistry,Lifecycle}.cs`, `doki.ps1`.

---

## 1. Recommendation

**Spine = the persona-first "DokiCharacters" approach (JUDGE 47/50).** It is the highest-leverage design that is still almost entirely clone-work over seams that already exist: file-based stores cloned verbatim from `References.cs`/`RecipeStore.cs`/`SavedSearches.cs` (with the unit-tested `RecipeStore.SafeName` guard), a pure unit-tested prompt-assembly core (`ChatPrompt.Build`) and lorebook activation (`Lorebook.Activate`) mirroring the `Director.ParseShotlist`/`Vision.ParseVerdict`/`Tts.ApplyLexicon` tested-core discipline, on-disk multi-turn conversations, and free voice readback by choosing the `agent` profile (the only llm-group profile carrying `tts` on `:8004`). It fits DokiDex's loopback/uncensored/single-user identity perfectly and ships a usable P0 with no streaming risk.

**Grafts:**
- **Transport + the "smallest shippable" P0 discipline** from the MINIMAL approach (JUDGE 41/50): ship a non-streaming `/api/chat` first (plain `fetch`, zero transport risk), then add streaming as an isolated, reversible upgrade. Use **SSE over a POST + `ReadableStream`** (not `EventSource`, not SignalR) so multi-turn JSON history rides in the POST body and no client library is shipped into the single-file SPA. JSON-wrap each delta (`data: {"t":...}`) so newlines/quotes survive SSE framing.
- **The symmetric GPU-arbitration guard** common to all four approaches: add `state.llmActive` beside the existing `state.mediaActive` in `pollStatus()`, plus `ensureLlm()`/`waitForLlm()`/`renderChatGuard()` as exact inversions of the shipped `ensureMedia()`/`waitForMedia()`/`renderGuard()`, reusing `/api/mode/{profile}` with **zero server change**.
- **The deferred-but-seamed roadmap** from the Open-WebUI-parity (46/50) and Creative-Director (45/50) approaches: tool-calling, hybrid RAG (the embed server `:8090` is group=`llm`, so code-RAG is resident exactly when chat is), and the in-process agent loop are explicitly **out of P0** but their seams are noted so later phases graft on cleanly. The transport and guard scaffolding are designed to be agent-loop-ready.

Why this spine over the more ambitious agentic designs: the agentic approaches scored higher on *leverage* but carry the project's own measured risk — `decisions.md` records open-model tool-call flakiness as the list grows, and tool-calling streaming is the single net-new code path with no in-repo template. The persona spine banks a real, on-identity assistant at the floor of risk, while the phased plan keeps every higher-value seam reachable.

---

## 2. Chosen Architecture

### 2.1 New endpoints (registered as lambdas inside `StudioHost.MapApi`, under `var api = app.MapGroup("/api")`)

Spliced beside the closest analogs (Director at `StudioHost.cs:284`; References at `:448`; Recipes at `:456`). No changes to `StudioHost.Build`'s DI registration — every store is `static` file-based exactly like `References`/`RecipeStore`/`SavedSearches`.

**Chat (P0, non-streaming — mirrors the `/api/director/shotlist` 503 contract at `StudioHost.cs:284-290`):**
```csharp
api.MapPost("/chat", async (ChatRequest body, CancellationToken ct) => {
    var r = await Chat.SendAsync(body, LlmTiers.Resolve(body.Tier), ct);
    return r.Ok ? Results.Json(new { conversation = r.ConversationId, text = r.Text })
                : Results.Json(new { error = r.Message }, statusCode: 503); // canonical "start agent mode first"
});
```
`ChatRequest(string? Conversation, string? Persona, string? Message, string? Tier)`. The server persists history in `ChatStore`, so the SPA need not resend the full transcript; an optional `Messages[]` is accepted for stateless callers (symmetry with how Director takes raw input).

**Chat (P1, streaming — POST + SSE response body, read in the browser with `res.body.getReader()`):**
```csharp
api.MapPost("/chat/stream", async (ChatRequest body, HttpContext ctx) => {
    ctx.Response.Headers.ContentType  = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var any = false;
    await foreach (var delta in Chat.StreamAsync(body, LlmTiers.Resolve(body.Tier), ctx.RequestAborted)) {
        if (delta is { Length: > 0 }) { any = true;
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { t = delta })}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted); } }
    if (!any) await ctx.Response.WriteAsync("event: error\ndata: LLM not reachable at :8080 — start agent mode first\n\n", ctx.RequestAborted);
    await ctx.Response.WriteAsync("event: done\ndata: end\n\n", ctx.RequestAborted);
});
```
Note: once headers flush (HTTP 200) the LLM-down case cannot be a 503, so the stream path emits an in-band `event: error` frame with the canonical string; the **non-stream `/api/chat` keeps the real 503** (matching Director). The SPA handles both.

**Persona / Lorebook / Conversation CRUD** — direct clones of `/api/references` (`StudioHost.cs:448`), `/api/recipes` (`:456`), and `/api/searches`:
```
GET/GET{name}/POST/DELETE  /api/personas
GET/GET{name}/POST/DELETE  /api/lorebooks
GET/GET{id}/DELETE         /api/chats        // server-generated id ⇒ no client path ⇒ no traversal
```

**Voice (reuse, no new endpoint):** assistant-turn readback calls the existing `POST /api/speak` (`StudioHost.cs:506`) with `{ text, voice: card.Voice }`; `GET /api/voices` (`:505`) populates the picker.

**No change to `/api/mode/{profile}`** (`StudioHost.cs:88`). It stays the one true direct evict+switch (`DokiService.Up → Lifecycle.Up`); the eviction-confirm lives in the browser.

### 2.2 Streaming transport decision — **SSE over POST + `ReadableStream`** (not SignalR, not `EventSource`)

The grounding offers SignalR-reuse as one option; I deliberately diverge, grounded in what the SPA already wires:

- **The SPA loads zero client libraries.** `control/Web/wwwroot/index.html` is one inline `<script>`, pure `fetch`/JSON — confirmed no `signalr`/`HubConnection`/`EventSource`/`getReader` anywhere. SignalR would force embedding `signalr.min.js` as a second embedded resource into the single-file `DokiDex.studio.index.html` and wiring a `HubConnection` that does not exist — strictly *more* work than the lift warrants. `StudioHub` stays reserved for the gen bridge (`GenerationJobs.Push → "job"`).
- **`EventSource` is GET-only** and cannot carry a multi-turn JSON history body cleanly. So I use `fetch('/api/chat/stream', {method:'POST', body: history})` and read `res.body.getReader()` (~15 lines, no library).
- **Security fit:** a same-origin POST sends a localhost `Origin` and passes `LocalSecurityMiddleware`'s state-changing-verb check (every existing POST in the app already does this). No middleware change.
- **1:1 mapping:** the C# side forwards llama-swap's own upstream OpenAI SSE deltas, one streaming format end-to-end.
- **P0 ships non-streaming** (plain `fetch`), so the highest-risk path is isolated to P1 and is reversible (could fall back to NDJSON without touching P0).

### 2.3 LocalLlm / LlmTiers reuse

- `LocalLlm.Body(object[] messages, …)` is **already array-based**, so multi-turn is a one-line public-surface widening:
  - `ChatTurnsAsync(IReadOnlyList<object> messages, double temp, int maxTokens, CancellationToken ct, string? model)` — thin `PostAsync(Body(messages.ToArray(), …))`, mirrors the existing `ChatAsync`.
  - `ChatStreamAsync(IReadOnlyList<object> messages, …, CancellationToken ct)` returning `IAsyncEnumerable<string>` — sets `body["stream"]=true`, sends with `HttpCompletionOption.ResponseHeadersRead`, reads `resp.Content` as a stream, and yields `choices[0].delta.content` parsed from each `data:` line via a **pure `ParseSseDelta(string line)→string?`** helper (`[DONE]`/keepalive → null) — the fragile-bit-made-pure, unit-tested like `Director.ParseShotlist`.
- The existing one-shot `ChatAsync`/`ChatVisionAsync` stay **untouched** (Director, Rewriter, Vision, multichar keep working).
- `LlmTiers.Resolve(tier)` routes Fast (`coder-fast`) / Quality (`coder-big`) / Vision verbatim; the chat DTO carries `Tier` and feeds it identically to Director/compose/pitchdeck. `ChatResult(bool Ok, string Text, string? Error)` is reused as the degradation contract.

---

## 3. SPA Changes (all in `control/Web/wwwroot/index.html`)

Five mechanical edits, each pinned to a real line. Theme via the existing `:root` vars (`--surface2`/`--surface3`/`--border`/`--cyan`); all LLM/user text through `esc()`; DOM built with the `$()` helper.

**A. Nav button** (after `index.html:80`, beside Director):
```html
<button id="nav-chat" onclick="setView('chat')">Chat</button>
```

**B. Section** (`<section id="view-chat" style="display:none">` under `<main>`, structured like the Director section): `<h2>Chat</h2>`, a `<div id="chatGuard">` banner slot, a persona `<select id="chatPersona">` + inline card editor, a conversation rail `<select id="chatThread">` + "+ New chat", a scrollable `<div id="chatLog">` of bubbles (user `--surface3` / assistant `--surface2`, both `1px --border`; a small 🔊 per assistant bubble), and a composer row: `<textarea id="chatInput">` + the **Director-style tier selector verbatim** `<select id="chatTier"><option value="fast">fast</option><option value="quality">quality · slower</option></select>` + `<button class="primary" onclick="sendChat()">Send</button>` + `<button class="ghost" onclick="newChat()">New</button>` + `<span class="hint" id="chatHint">`.

Bubble CSS added to `<style>`:
```css
.bub{max-width:80%;padding:9px 13px;border-radius:12px;line-height:1.5;white-space:pre-wrap;word-break:break-word}
.bub.user{align-self:flex-end;background:var(--surface3);border:1px solid var(--border)}
.bub.assistant{align-self:flex-start;background:var(--surface2);border:1px solid var(--border)}
.bub.assistant.err{border-color:var(--gold);color:var(--gold)}
```

**C. Register** — add `'chat'` to the hard-coded array at `index.html:341` (the array IS the router; a missing entry throws on `getElementById`). Add lazy-init in `setView`: `if (v==='chat'){ loadPersonas(); loadThreads(); document.getElementById('chatInput').focus(); }`.

**D. State + GPU flag** — extend the `state` object (`index.html:338`) with `llmActive:false`. In `pollStatus()` (after the `state.mediaActive = active==='media'` line at `index.html:1198`) add `state.llmActive = active==='llm'; renderChatGuard();`.

**E. Send loop** (P0 non-streaming; mirrors `storyboard()` and the `ensureMedia()` gate at `index.html:1039`):
```js
let _chatMsgs=[], _chatConv=null;
function appendBubble(role,text){ const log=document.getElementById('chatLog');
  const b=$(`<div class="bub ${role}"></div>`); b.textContent=text||''; log.appendChild(b); log.scrollTop=log.scrollHeight; return b; }
function newChat(){ _chatMsgs=[]; _chatConv=null; document.getElementById('chatLog').innerHTML=''; document.getElementById('chatHint').textContent=''; }
async function ensureLlm(){ if(state.llmActive) return true;
  if(!confirm("The GPU is in MEDIA mode. Switch to AGENT now (this stops SwarmUI / image+video) and chat?")) return false;
  try{ await fetch('/api/mode/agent',{method:'POST'}); }catch{} return await waitForLlm(90); }
function waitForLlm(maxSec){ return new Promise(res=>{ let n=0; const t=setInterval(()=>{ if(state.llmActive){clearInterval(t);res(true);} else if(++n>=maxSec){clearInterval(t);res(false);} },1000); }); }
async function sendChat(){
  const inp=document.getElementById('chatInput'), hint=document.getElementById('chatHint');
  const text=inp.value.trim(); if(!text) return;
  if(!await ensureLlm()){ hint.textContent='chat needs AGENT mode'; return; }
  appendBubble('user',text); _chatMsgs.push({role:'user',content:text}); inp.value=''; hint.textContent='thinking…';
  try{
    const r=await fetch('/api/chat',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({conversation:_chatConv, persona:document.getElementById('chatPersona').value,
        message:text, tier:document.getElementById('chatTier').value})});
    const j=await r.json();
    if(!r.ok){ const b=appendBubble('assistant', j.error||('error '+r.status)); b.classList.add('err'); hint.textContent=''; return; }
    _chatConv=j.conversation; _chatMsgs.push({role:'assistant',content:j.text}); appendBubble('assistant', j.text); hint.textContent='';
  }catch{ hint.textContent='request failed'; }
}
```
P1 swaps the body for the streaming reader: append an empty assistant bubble, `res.body.getReader()`+`TextDecoder`, split on `\n\n`, `JSON.parse(frame.slice(6)).t` per `data:` frame, append into the live bubble; on `event: error` mark the bubble `.err`.

**F. GPU guard + Ctrl+Enter** — `renderChatGuard()` clones `renderGuard()` (`index.html:1178`) inverted ("The GPU is in MEDIA — chat needs AGENT [Switch to AGENT]" → POST `/api/mode/agent`). Add a `keydown` listener on `#chatInput` cloning `index.html:1213`: `if(e.key==='Enter'&&(e.ctrlKey||e.metaKey)){e.preventDefault();sendChat();}`. The Status view already labels `agent` as "chat + code" (`index.html:1202`) — no change needed; it now tells the truth.

---

## 4. GPU-Arbitration UX

The single 32 GB GPU runs exactly ONE group at a time (`doki.ps1` `$Services`/`$Profiles`, mirrored 1:1 in `ServiceRegistry.cs`, kept in sync by `ControlPlaneTests`). `Lifecycle.Up(profile)` force-evicts the opposite group (26 GB llama-swap + 18 GB SwarmUI > 32 GB — no coexistence).

| Mode | Active group | `:8080` (llama-swap) | Chat | Behavior on a chat send |
|---|---|---|---|---|
| **agent** (`llama-swap, tts, stt, embed`) | `llm` | up | ✅ available | sends directly; voice (`:8004`) + future STT available |
| **coexist** (`llama-swap, fim, embed`) | `llm` | up | ✅ available | sends directly (chat + FIM; no voice unless agent) |
| **media** (`media, prompt-rewriter`) | `media` | evicted | ❌ down | `ensureLlm()` → confirm → `POST /api/mode/agent` → `waitForLlm(90)` → send; decline ⇒ canonical "start agent mode first" |

**Reuse, not reinvention:** the chat send path is the exact inverse of the shipped generate guard.
- `state.llmActive = (gpu.activeGroup==='llm')` is added beside `state.mediaActive` in `pollStatus()` — the grounding's named "missing piece."
- `ensureLlm()`/`waitForLlm()`/`renderChatGuard()` are clones of `ensureMedia()` (`index.html:1039`)/`waitForMedia()` (`:725`)/`renderGuard()` (`:1178`), polling `activeGroup==='llm'`.
- The server stays "explicit intent switches directly" — **no silent auto-switch is added to `/api/chat`**. The `confirm()` is the user's intent gate, in the browser. When `:8080` is down and the user declined, `/api/chat` returns 503 + the canonical string (same contract as Director at `StudioHost.cs:289`, same string as `LocalLlm` PostAsync).
- **Default chat profile = `agent`** (the only llm-profile carrying `tts:8004`), so voice readback rides for free with no extra GPU gate.
- **`coexist` is NOT chat-alongside-media** — it is an all-`llm`-group profile; the surface never promises concurrent chat + gen. Switching is destructive-by-design; transcripts survive because they are JSON-on-disk in `ChatStore`.

---

## 5. Persona / Memory Model (single-user, uncensored)

All file-based under `<RepoPaths.Root>/`, `RecipeStore.SafeName`-guarded, graceful try/catch, JSON via the `SavedSearches` serializer — zero new persistence tech, no DB, no DI, plain editable files matching the project's file-based discipline.

**1. Character Cards** (`personas/<name>.json`, `Persona.cs`) — the GPTs analog, local + uncensored. `PersonaCard(Name, Avatar, System, Persona, Greeting, Examples, Tier, Voice, Lorebook)`: `System` = the character's behavior/voice (uncensored system prompt, no content filter beyond the loaded model's own); `Persona` = the user's own identity block (SillyTavern `{{user}}`); `Greeting` auto-seeds a fresh thread; `Examples` = few-shot dialogue folded into the system bundle; `Voice` = a `Tts.Voices()` name for readback; `Lorebook` = an attached lorebook name. A built-in default uncensored, terse, studio-aware persona ships in code if no card is selected, so raw chat works with zero setup.

**2. Lorebook-lite / World Info** (`lorebooks/<name>.json`, `Lorebook.cs`) — keyword-triggered context injection. The activation is the **pure unit-tested core**: `static IReadOnlyList<LoreEntry> Activate(entries, scanText, maxEntries, maxChars)` — whole-word, case-insensitive key match (reusing the `\b{Regex.Escape(key)}\b` technique already proven in `Tts.ApplyLexicon`), enabled-only, de-dup, char-budget cap. Activated entries are injected as one `[World Info]` system turn placed after the card system bundle and before history. `LorebookTests.cs` pins hit/miss, case-insensitivity, disabled-skip, budget-truncation, empty-keys-never-fire.

**3. Prompt assembly** (`ChatPrompt.cs`, pure) — `Build(card, activeLore, history, userMessage, historyTurnBudget) → List<object>` (OpenAI message[]): system bundle (`card.System` + `card.Persona` + examples) → lorebook injection → most-recent-wins history trim (bounds `max_tokens`) → user turn. The single source of truth for what reaches llama-swap; `ChatPromptTests.cs` covers card-only, card+lore ordering, history trim, empty-card path.

**4. Conversations** (`chats/<id>.json`, `ChatStore.cs`) — `Conversation(Id, Persona, Lorebook, Created, Messages[{Role,Content,Ts}])`, list/load/save/delete mirroring `SavedSearches`; server-generated id (no traversal). Append-on-turn; reload restores. This is the multi-turn persistence the one-shot `LocalLlm` structurally lacks.

**5. Orchestrator** (`Chat.cs`, mirrors `Director`/`Rewriter` shape) — loads card + lorebook, runs `Lorebook.Activate` over the recent transcript, calls `ChatPrompt.Build`, then `ChatTurnsAsync` (P0) or `ChatStreamAsync` (P1), persists both turns, returns a `Result` mapped exactly like Director's.

Deliberately **NOT** built into the persona model: vector/semantic long-term memory, auto-summarization, tool-calling. Keyword lorebook + trimmed transcript is the memory model and is sufficient for the persona use case while staying inside the testable-pure-core discipline.

---

## 6. Assistant-Contract Scope, Staged

| Capability | Phase | Net-new vs reuse |
|---|---|---|
| Multi-turn persistent chat + persona + tier toggle, non-streaming | **P0** | `ChatTurnsAsync` (1-line widening); stores clone References/SavedSearches |
| GPU-arbitration guard (llm-side mirror) | **P1** | SPA-only clones of the media guard |
| Token streaming (SSE over POST + `getReader`) | **P2** | `ChatStreamAsync` + pure `ParseSseDelta` — the one genuinely new code path |
| Lorebook-lite / World Info | **P3** | pure `Lorebook.Activate` + `LorebookTests` |
| Voice readback | **P4** | reuse `/api/speak`+`/api/voices`, zero TTS code |
| Vision-in-chat (multimodal turns) | **P5** | reuse `Vision.cs` `image_url` shape + `LlmTiers.Vision` + `GalleryService.ImageDataUrl` |
| Tool-calling / agent loop / hybrid RAG | **Pn (deferred)** | greenfield; seams noted (`tools`/`tool_choice`, `tool_calls` accumulator, embed `:8090` code-RAG) |

---

## 7. Phased Build Plan

Each phase is independently shippable, leaves `dotnet test` green, and every new pure module ships its paired `*Tests.cs`. `ControlPlaneTests` and `doki.ps1` sync are untouched (no service/profile change — chat rides the existing `llm` group).

**P0 — Persistent multi-turn persona chat, non-streaming.**
Files: `control/Web/LocalLlm.cs` (+`ChatTurnsAsync`), new `control/Web/{Persona,ChatStore,ChatPrompt,Chat}.cs`, `control/Web/StudioHost.cs` (+`/api/personas` CRUD cloning `/api/references` `:448`, +`/api/chats` CRUD, +`POST /api/chat` cloning the Director 503 at `:284-290`), `control/Web/wwwroot/index.html` (nav button `:80`, view-chat section, `'chat'` in the array `:341`, `sendChat()`, `state.llmActive` + `ensureLlm`/`waitForLlm`/`renderChatGuard`, Ctrl+Enter).
Tests: `ChatPromptTests.cs` (card/history/empty-card ordering + trim), optional `PersonaTests` round-trip.
**Verify:** agent mode — create a card, send several turns, reload, reopen thread from `/api/chats` → history persists; media mode — sending prompts the AGENT-switch confirm; `curl -s -d '{"message":"hi"}' http://127.0.0.1:5111/api/chat` returns `{text}` in agent mode and **503 + "start agent mode first"** in media mode. `dotnet test` green.

**P1 — GPU-arbitration guard (SPA-only).** Files: `index.html`. Adds the `state.llmActive`/`waitForLlm`/`ensureLlm`/`renderChatGuard` clones. **Verify:** send in media → confirm → switch → wait → send; decline → start-agent banner.

**P2 — Token streaming.** Files: `LocalLlm.cs` (+`ChatStreamAsync` + pure `ParseSseDelta`), `Chat.cs` (`StreamAsync`), `StudioHost.cs` (+`POST /api/chat/stream`, `text/event-stream`), `index.html` (swap `sendChat` to the `getReader()` frame loop, keep non-stream as fallback). Tests: `ParseSseDelta` over captured llama-swap delta fixtures (`content` line, `[DONE]`, split-UTF-8 boundary). **Verify:** `curl -N` streams `data:` frames; tokens render incrementally in the bubble; killing `:8080` mid-app shows the gold error bubble.

**P3 — Lorebook-lite.** Files: new `control/Web/Lorebook.cs` (+pure `Activate` + `LorebookTests.cs`), wire into `ChatPrompt.Build`/`Chat.SendAsync`, `StudioHost.cs` (+`/api/lorebooks` CRUD cloning `/api/recipes` `:456`), `index.html` (entries editor). **Verify:** an entry whose keys appear in recent turns is injected (model references canon it wasn't told this turn); `LorebookTests` pins match/budget/disabled.

**P4 — Voice readback.** Files: `index.html` only (reuse `/api/speak` `:506` + `/api/voices` `:505`). **Verify:** 🔊 on an assistant turn synthesizes in the card's voice; clip appears in Library.

**P5 — Vision-in-chat.** Files: `LocalLlm.cs` (allow a turn's content to be the `Vision.cs` `{text + image_url}` array inside `ChatTurnsAsync`), `ChatPrompt.cs` (attach `GalleryService.ImageDataUrl`), tier forced to `LlmTiers.Vision`. **Verify:** drop a gallery image into chat, ask, get a grounded reply; degrades like `/api/describe` when no vision model.

**Pn — DEFERRED (build only on concrete need):** tool-calling (`tools`/`tool_choice` in `Body`, `tool_calls` accumulator, run→append `role:"tool"`→re-call loop), hybrid RAG over `code_index.db` via embed `:8090`, in-process agent loop with GPU handoff, persona auto-summary memory, retiring `pollJobs` for the already-broadcast `"job"` SignalR event.

---

## 8. Reused vs New

**Reused (verbatim or thin-wrapped):**
- `LocalLlm.Body(object[] messages, …)` — already array-based; multi-turn is one public method over it.
- `LlmTiers.Resolve(tier)→model` (Fast/Quality/Vision) — chat DTO carries `Tier` like Director/compose/pitchdeck.
- `References.cs`/`RecipeStore.cs`/`SavedSearches.cs` + `RecipeStore.SafeName` — Personas/Lorebooks/ChatStore are direct clones.
- The 503 + "start agent mode first" degradation contract (`Director` at `StudioHost.cs:284-290`; `LocalLlm` PostAsync `ChatResult`).
- `LocalSecurityMiddleware` (unchanged) — same-origin POST passes Host allowlist + Origin/CSRF.
- `StudioHub` (untouched) — chat uses SSE, not the hub.
- `ServiceRegistry.cs`/`Lifecycle.cs`/`/api/mode/{profile}` (`StudioHost.cs:88`) — the entire GPU evict+switch, reused inverted by the client.
- `Tts.SynthBytesAsync` via `/api/speak`+`/api/voices`; `Vision.cs` `image_url` shape + `GalleryService.ImageDataUrl` (P5); the `\b{Regex.Escape}\b` technique from `Tts.ApplyLexicon`.
- SPA: `setView` router array, `$()`/`esc()`, the Director tier `<select>`, `ensureMedia`/`waitForMedia`/`renderGuard`/`pollStatus`/`state.mediaActive`, the Ctrl+Enter listener, the `.banner`/`.kin`/`.primary`/`.ghost`/`.hint` classes, the `:root` theme vars.

**New (small, all mirroring existing analogs):**
- `LocalLlm.ChatTurnsAsync` (multi-turn), `ChatStreamAsync` (stream:true + `ResponseHeadersRead` + pure `ParseSseDelta`).
- `Persona.cs`, `Lorebook.cs` (+pure `Activate`), `ChatPrompt.cs` (pure `Build`), `ChatStore.cs`, `Chat.cs`.
- `/api/personas`, `/api/lorebooks`, `/api/chats` CRUD + `POST /api/chat` (+P2 `POST /api/chat/stream`).
- The Chat SPA view (markup, send/stream loop, history, persona/lorebook editors, `state.llmActive` + the three guard clones).
- Tests: `ChatPromptTests.cs`, `LorebookTests.cs`, `ParseSseDelta` fixtures.

---

## 9. Risks + Mitigations

1. **SSE framing of token deltas** — llama-swap deltas contain newlines/quotes; naive `data: <delta>` breaks the frame. **Mitigation:** JSON-wrap each delta (`data: {"t":...}`) via `System.Text.Json`; client `JSON.parse`. Covered by `ParseSseDelta` unit tests. P0 avoids this entirely (non-streaming).
2. **Can't return 503 after streaming starts** — once HTTP 200 flushes, LLM-down can't be a status. **Mitigation:** stream path emits in-band `event: error` with the canonical string; non-stream `/api/chat` keeps the real 503. Documented, not a defect.
3. **HttpClient timeout vs long generations** — the shared client has a 3-min timeout; `Timeout` spans the *whole* streamed operation in .NET, not just headers. **Mitigation:** the streaming path uses a dedicated/extended-timeout `HttpClient` (or no timeout) for `ChatStreamAsync`, bounded instead by `ctx.RequestAborted` (browser abort cancels the upstream read).
4. **GPU mode switch is destructive** — `ensureLlm()` force-evicts SwarmUI (kills in-flight gen). **Mitigation:** the `confirm()` spells out "this stops SwarmUI / image+video" (mirrors the shipped generate confirm); never auto-switch server-side; transcripts are JSON-on-disk so chat state survives the flip.
5. **`max_tokens` overflow on long threads** — history grows unbounded in `ChatStore`. **Mitigation:** `ChatPrompt.Build` trims to a most-recent turn budget (pure + tested). Auto-summarization deferred.
6. **Lorebook false-positives / budget blowout** — substring matching over-fires. **Mitigation:** whole-word case-insensitive only, enabled-gating, `maxEntries`+`maxChars` cap, all pinned by `LorebookTests`.
7. **Uncensored posture** — the persona carries no refusal scaffolding; if the *loaded* model refuses, that's a model property surfaced as ordinary assistant text, not an app bug. No new exposure (same loopback + `LocalSecurityMiddleware` guard; no new port, no auth).
8. **Filename collision / traversal** — all names go through `RecipeStore.SafeName`; conversation ids are server-generated. No new traversal surface.
9. **Test/recipe discipline** — no new service/profile, so `ControlPlaneTests`/`doki.ps1` sync stays green (must not touch `ServiceRegistry`). Every new pure module ships its paired `*Tests.cs`; network wrappers stay thin and degrade gracefully like Director/Rewriter/Tts.

---

## 10. Explicitly Out of Scope (v1, P0–P5)

- **SignalR client embedding** — SSE chosen; `StudioHub` stays the gen bridge only.
- **Tool-calling / function-calling / the agent loop** — `tools`/`tool_choice` body fields, `tool_calls` parsing, and the call→run→append→re-call loop are confirmed nonexistent and are NOT added; deferred to Pn with seams noted.
- **RAG / hybrid retrieval** over `code_index.db` / embed `:8090` — deferred (the seam is free because embed is group=`llm`, resident exactly when chat is).
- **Server-side silent mode auto-switch** — deliberately kept in the browser; `/api/mode/{profile}` stays direct-explicit-intent.
- **Vector/semantic long-term memory, auto-summarization of old turns** — keyword lorebook + trimmed transcript is the v1 memory model.
- **Cross-surface "generate from chat" handoff** (the Creative-Director angle) — a strong later phase, but its suspend/resume + GPU ping-pong is out of v1; the guard scaffolding built in P1 makes it reachable.
- **Multi-user / auth** — single-user loopback only; no auth beyond `LocalSecurityMiddleware`.
- **Retiring `pollJobs` for the broadcast `"job"` event** — a noted free win, not part of this design.

