using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DokiDex.Web;

// A browser request for one chat turn. Conversation = an existing server id (null/empty => start a fresh thread);
// Persona = a saved card name (null => the built-in default persona); Message = the user's new turn; Tier = the
// speed/quality LlmTiers tier. Messages is an optional client-supplied transcript for stateless callers
// (symmetry with how Director takes raw input) — when present and the server has no stored thread, it seeds
// history; the server otherwise persists history itself in ChatStore so the SPA need not resend the transcript.
// Image (P5 vision-in-chat, optional) = a GALLERY IMAGE NAME (resolved server-side to a data: URL via
// GalleryService.ImageDataUrl). When present + resolvable, the turn is answered by the VISION model (the tier is
// forced to LlmTiers.Vision regardless of the requested speed Tier); an unresolvable name is ignored (text-only).
// Tools (Pn, default false) routes the turn through the bounded TOOL-CALLING agent loop (Chat.AgentAsync) with
// the curated single-tool registry (ChatTools) instead of the plain Chat.SendAsync path. Tools=false keeps the
// exact current non-tool behavior. Streaming + tools is a later slice (the stream endpoint ignores Tools).
public sealed record ChatRequest(
    string? Conversation, string? Persona, string? Message, string? Tier, IReadOnlyList<ChatTurn>? Messages = null,
    string? Image = null, bool Tools = false);

// The body for POST /api/chats/{id}/edit: replace the USER turn at Index with Content (the SPA then re-runs
// regenerate so the edited question gets a fresh answer). Index is the 0-based conv.Messages position (== the
// SPA's _chatMsgs index). The endpoint validates Index in range AND that it points at a user turn before Save.
public sealed record ChatTurnEditRequest(int Index, string? Content);

// The OPTIONAL body for POST /api/chats/{id}/regenerate: a transient per-resend OVERRIDE of the persona and/or
// tier. Both null (or an absent body) => regenerate with the thread's stored Persona + the default tier (the
// v0.22 behavior, byte-for-byte). Persona names a saved card (null => keep conv.Persona); Tier names an LlmTiers
// tier (null => default). The override is transient: the stored conv.Persona on disk is NOT rewritten.
public sealed record ChatRegenerateRequest(string? Persona, string? Tier);

// The body for POST /api/chats/{id}/branch: the 0-based conv.Messages index to fork at (same index space as
// /edit and /turn/{index}). The fork keeps the prefix UP TO AND INCLUDING this turn (ChatEdit.BranchAtTurn).
public sealed record ChatBranchRequest(int Index);

// The persona-chat orchestrator (mirrors Director/Rewriter shape): load the card + the conversation, assemble
// the prompt via the pure ChatPrompt.Build, run the multi-turn LLM call, persist BOTH turns, and return a
// Director-style Result. The network call degrades gracefully (LLM down => Ok=false + the canonical message),
// so the endpoint maps !Ok to the same 503 "start agent mode first" contract Director uses.
public static class Chat
{
    // Most-recent turns folded into each request (bounds max_tokens). Full transcript still persists on disk.
    private const int HistoryTurnBudget = 20;

    // P3 lorebook caps: at most this many activated [World Info] entries / cumulative chars per request.
    private const int LoreMaxEntries = 8;
    private const int LoreMaxChars = 1500;

    // (KB retrieval top-K lives in DocSearch.RetrieveK — the single knob — so there's no duplicate constant here.)

    // Steps defaults to empty so the existing SendAsync callers/tests are unaffected; AgentAsync fills it with
    // the tool calls taken (each: the tool name + a short preview of its result) for an optional SPA trace.
    // KbId (default null) carries the conversation's EFFECTIVE attached KB after the turn — the v0.16 default-KB
    // follow-up: a FRESH thread may have just auto-attached the GLOBAL default KB (ApplyDefaultKb), but the SPA only
    // learns the thread's kbId from renderThread (a reload). Surfacing it on the send response lets the SPA refresh
    // _chatKbId after a fresh send with NO extra round-trip, so renderKbScope hides the private-doc box + shows the
    // correct "library: <name>" pill (the FIX-1 orphan/wrong-pill regression). Null when no KB is attached (the
    // no-default / no-KB path is byte-for-byte: the SPA reads null and keeps the "private to this chat" box).
    public sealed record Result(bool Ok, string ConversationId, string Text, string? Message,
        IReadOnlyList<ToolStep>? Steps = null, string? KbId = null);

    // One executed tool hop in the agent loop (for an optional SPA "tools taken" trace): the tool name, the raw
    // arguments JSON the model supplied, and the tool result text fed back as the role:"tool" turn.
    public sealed record ToolStep(string Tool, string ArgumentsJson, string Result);

    // The bounded agent loop's hop cap: at most this many tool-execution rounds before the loop stops and returns
    // the last content (or a graceful "stopped after N steps"). A small cap is the open-model mitigation from
    // decisions.md — a model that keeps requesting tools can never spin forever.
    public const int MaxToolHops = 4;

    // Overall wall-clock budget for ONE agent turn. AgentAsync can drive up to (MaxToolHops + 1) SEQUENTIAL LLM
    // calls, each otherwise bounded only by the shared 3-min HttpClient timeout — so N stacked timeouts could reach
    // ~15 min. A single linked CTS (this budget AND the incoming ct) caps the WHOLE turn instead; on expiry the loop
    // ends gracefully and returns whatever text accumulated (or the canonical "stopped" message).
    private static readonly TimeSpan AgentTurnTimeout = TimeSpan.FromMinutes(4);

    // Tool-calling runs MUCH lower temperature than free-form chat: open models pick tools far more reliably when
    // near-deterministic (research §5.4: tool-calling wants temp 0.0-0.1 + min_p 0.1; serving/test-toolcall.ps1
    // gated coder-fast at 0.2). LocalLlm.ChatToolsAsync pairs this with min_p 0.1 + top_p 0.9. Distinct from
    // SendAsync/StreamAsync's 0.8 conversational temp.
    private const double ToolTemperature = 0.1;

    // One streamed event from StreamAsync. The FIRST event is always a Meta carrying the conversation id (so the
    // SPA can capture it before any token arrives) AND the conversation's effective KbId (so the SPA can refresh
    // _chatKbId on a fresh send — the FIX-1 default-KB orphan/wrong-pill fix, mirroring Result.KbId); every
    // subsequent event is a Delta carrying one token chunk.
    //   Meta(conversationId, kbId): IsMeta=true,  ConversationId set, KbId set-or-null, Delta=null
    //   Token(delta):               IsMeta=false, Delta set
    public sealed record StreamEvent(bool IsMeta, string? ConversationId, string? Delta, string? KbId = null)
    {
        public static StreamEvent Meta(string id, string? kbId = null) => new(true, id, null, kbId);
        public static StreamEvent Token(string delta) => new(false, null, delta);
    }

    public static async Task<Result> SendAsync(ChatRequest body, string? model, GalleryService gallery, CancellationToken ct)
    {
        var userMessage = (body?.Message ?? "").Trim();
        if (userMessage.Length == 0) return new Result(false, "", "", "empty message");

        var card = Persona.Load(body!.Persona);   // null name / unknown => built-in default persona

        // Load the existing thread, or start a new one (server-generated id => no client path => no traversal). A
        // freshly-created thread picks up the global DEFAULT KB (ApplyDefaultKb) so project docs ride in every new
        // chat; with no default set this is a no-op (KbId stays null) and the no-KB path is byte-for-byte.
        var conv = ChatStore.Load(body.Conversation)
                   ?? ApplyDefaultKb(ChatStore.NewConversation(body.Persona, card?.Lorebook));

        // Prior turns: the persisted transcript, or a stateless caller's supplied Messages when there's none.
        IReadOnlyList<ChatTurn> history = SelectHistory(conv, body.Messages);

        var activeLore = ActivateLore(card?.Lorebook, history, userMessage);

        // P5 vision-in-chat: resolve an attached gallery image to a data: URL. When it resolves we (a) attach it as
        // the multimodal user-turn content and (b) FORCE the Vision model/tier (vision needs the VL block regardless
        // of the requested speed tier). An unresolvable/absent image => text-only with the originally requested tier.
        var imageDataUrl = ResolveImage(body.Image, gallery);
        model = VisionModel(imageDataUrl, model);

        // KB context-injection: retrieve the conversation's top-K relevant doc chunks for the latest turn (null +
        // no injection when there's no attached KB / the embed server is down — the no-KB path stays unchanged).
        var activeDocs = await RetrieveDocs(conv.KbId, userMessage, ct).ConfigureAwait(false);
        var activeMemories = await MemoryRecall.RetrieveAsync(ct).ConfigureAwait(false);   // long-term [Memory] recall (global; gated on the store existing, degrades to empty)

        var messages = ChatPrompt.Build(card, history, userMessage, HistoryTurnBudget, activeLore, imageDataUrl, activeDocs, activeMemories);

        var temperature = 0.8;
        var maxTokens = 1024;
        var chat = await LocalLlm.ChatTurnsAsync(messages, temperature, maxTokens, ct, model).ConfigureAwait(false);
        if (!chat.Ok) return new Result(false, conv.Id, "", chat.Error);

        var reply = (chat.Text ?? "").Trim();

        // Persist BOTH turns so reload restores the multi-turn thread.
        var nowUser = DateTime.UtcNow.ToString("o");
        var nowAsst = DateTime.UtcNow.ToString("o");
        var appended = new List<ChatTurn>(conv.Messages)
        {
            new("user", userMessage, nowUser),
            new("assistant", reply, nowAsst),
        };
        conv = conv with { Messages = appended };
        ChatStore.Save(conv);   // graceful: a save failure does not fail the reply the user already has

        // Carry conv.KbId so a FRESH send that auto-attached the default KB lets the SPA refresh _chatKbId with no
        // extra round-trip (FIX 1); null when no KB is attached (the no-default path is byte-for-byte for the SPA).
        return new Result(true, conv.Id, reply, null, KbId: conv.KbId);
    }

    // PURE loop-termination decision (no GPU/disk): take another hop only when the model requested at least one
    // tool AND we are still under the hop cap. No tool_calls => the model's content IS the answer (graceful
    // fallthrough, the open-model mitigation). hop is the number of hops ALREADY taken. Total + unit-tested so the
    // bound can't silently drift.
    public static bool ShouldContinue(IReadOnlyList<LocalLlm.ToolCall> toolCalls, int hop, int maxHops)
        => toolCalls is { Count: > 0 } && hop < maxHops;

    // PURE per-hop transcript shaping (no GPU/disk): append ONE assistant tool-call turn followed by ONE role:"tool"
    // result message per executed call onto the mutable working transcript, in assistant-THEN-results order. The
    // assistant turn carries content:NULL (OpenAI convention for a tool-call message — not "") plus the tool_calls;
    // each result echoes the SAME tool_call_id its call carried (ids are synthesized upstream in ParseToolCalls when
    // the model omits one, so correlation always holds and id-less calls can't collide). `results[i]` is the tool
    // output for `toolCalls[i]`; a short results array is tolerated (a missing result => "" content). Extracted from
    // AgentAsync so this wire-shape is unit-testable without a live model.
    public static void AppendToolRound(
        List<object> working, string? assistantContent,
        IReadOnlyList<LocalLlm.ToolCall> toolCalls, IReadOnlyList<string> results)
    {
        working.Add(new
        {
            role = "assistant",
            content = (string?)null,   // tool-call assistant turn => content is null, not ""
            tool_calls = toolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new { name = tc.Name, arguments = tc.ArgumentsJson },
            }).ToArray(),
        });
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            var content = i < results.Count ? results[i] : "";
            working.Add(new { role = "tool", tool_call_id = tc.Id, name = tc.Name, content });
        }
    }

    // TOOL-CALLING agent loop (Pn, non-streaming): the agentic twin of SendAsync. Builds the prompt via the pure
    // ChatPrompt.Build, then calls LocalLlm.ChatToolsAsync (same Body + a 'tools' array + tool_choice:"auto").
    // While the model returns tool_calls AND hops remain: append the assistant tool-call message + one role:"tool"
    // result message per call (tool_call_id + ChatTools.Run output) and RE-CALL. Stops when the model returns
    // content with no tool_calls (that content is the answer) OR MaxToolHops is hit (returns the last content, or a
    // graceful "stopped after N steps" if none). Persists the user + final assistant turns exactly like SendAsync
    // (reusing SelectHistory + the empty-message guard). The live LLM call degrades like SendAsync when :8080 is
    // down (Ok=false + the canonical message => the endpoint maps to the same 503 contract).
    public static async Task<Result> AgentAsync(ChatRequest body, string? model, GalleryService gallery, CancellationToken ct)
    {
        var userMessage = (body?.Message ?? "").Trim();
        if (userMessage.Length == 0) return new Result(false, "", "", "empty message");

        var card = Persona.Load(body!.Persona);   // null name / unknown => built-in default persona

        // A freshly-created thread picks up the global DEFAULT KB (no-op when none is set — byte-for-byte no-KB).
        var conv = ChatStore.Load(body.Conversation)
                   ?? ApplyDefaultKb(ChatStore.NewConversation(body.Persona, card?.Lorebook));

        IReadOnlyList<ChatTurn> history = SelectHistory(conv, body.Messages);
        var activeLore = ActivateLore(card?.Lorebook, history, userMessage);

        // KB context-injection (same as SendAsync): the attached docs are injected unconditionally each turn,
        // independent of the tool registry — the [Documents] block rides alongside any tool calls the loop makes.
        var activeDocs = await RetrieveDocs(conv.KbId, userMessage, ct).ConfigureAwait(false);
        var activeMemories = await MemoryRecall.RetrieveAsync(ct).ConfigureAwait(false);   // long-term [Memory] recall (global; gated on the store existing, degrades to empty)

        // The agent loop is text-only (no vision-in-chat this slice): keep the requested speed tier as-is.
        var messages = ChatPrompt.Build(card, history, userMessage, HistoryTurnBudget, activeLore, activeDocs: activeDocs, activeMemories: activeMemories);

        // A MUTABLE working transcript for the loop: we append the assistant tool-call turns + tool results onto a
        // copy of the built messages, then re-call. The persisted conversation only ever stores the user turn + the
        // FINAL assistant answer (tool plumbing is an implementation detail, not chat history).
        var working = new List<object>(messages);

        var maxTokens = 1024;
        var steps = new List<ToolStep>();
        string finalText = "";

        // Bound the WHOLE turn (not each hop): one linked CTS combining the caller's ct with AgentTurnTimeout, so up
        // to MaxToolHops+1 sequential LLM calls can't stack N×3-min timeouts. On budget expiry we return whatever
        // text accumulated (or the canonical "stopped" message) — graceful, like the hop-cap fallthrough.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(AgentTurnTimeout);
        var loopCt = turnCts.Token;

        for (var hop = 0; ; hop++)
        {
            var turn = await LocalLlm.ChatToolsAsync(working, ChatTools.ToolsJson, ToolTemperature, maxTokens, loopCt, model)
                .ConfigureAwait(false);
            if (!turn.Ok)
            {
                // Our overall budget elapsed (the caller's ct is NOT cancelled, only our linked timeout fired): end
                // gracefully with what we have so far rather than surfacing a bare error / a 503.
                if (turnCts.IsCancellationRequested && !ct.IsCancellationRequested) break;
                return new Result(false, conv.Id, "", turn.Error);
            }

            finalText = (turn.Content ?? "").Trim();

            if (!ShouldContinue(turn.ToolCalls, hop, MaxToolHops))
                break;   // no tool_calls (content is the answer) OR hop cap reached

            // Execute each requested tool, recording a ToolStep trace, then echo the assistant's tool-call turn +
            // one role:"tool" result per call onto the working transcript via the pure AppendToolRound (content:null
            // on the assistant turn; the synthesized-or-real tool_call_id correlates each result to its call).
            var results = new List<string>(turn.ToolCalls.Count);
            foreach (var tc in turn.ToolCalls)
            {
                var result = ChatTools.Run(tc.Name, tc.ArgumentsJson, gallery, conv.Id);
                steps.Add(new ToolStep(tc.Name, tc.ArgumentsJson, result));
                results.Add(result);
            }
            AppendToolRound(working, turn.Content, turn.ToolCalls, results);
        }

        // Graceful: if the model never produced text (exhausted its hops on tools, or the overall turn timed out
        // mid-flight), say so rather than persist an empty turn the user can't read.
        var reply = finalText.Length > 0
            ? finalText
            : (steps.Count > 0
                ? $"(stopped after {steps.Count} tool step(s) without a final answer)"
                : "(the assistant did not return an answer in time)");

        // Persist BOTH turns (user + final assistant), exactly like SendAsync, so reload restores the thread.
        var now = DateTime.UtcNow.ToString("o");
        var appended = new List<ChatTurn>(conv.Messages)
        {
            new("user", userMessage, now),
            new("assistant", reply, now),
        };
        conv = conv with { Messages = appended };
        ChatStore.Save(conv);   // graceful: a save failure does not fail the reply the user already has

        // Same FIX-1 KbId surface as SendAsync (the tools path also runs through ApplyDefaultKb on a fresh thread).
        return new Result(true, conv.Id, reply, null, steps, KbId: conv.KbId);
    }

    // STREAMING send (P2): the streaming twin of SendAsync. Loads the card + conversation (reusing SelectHistory
    // and the same empty-message guard), assembles the prompt via the pure ChatPrompt.Build, then streams the
    // upstream content deltas to the caller WHILE accumulating the full reply; when the stream completes it
    // persists BOTH turns (user + accumulated assistant) exactly like SendAsync. The FIRST yielded event is a
    // Meta carrying the (server-generated) conversation id so the endpoint can frame it before any token; each
    // later event is one token Delta. Zero deltas (LLM down / no model) => the caller emits the in-band error.
    public static async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatRequest body, string? model, GalleryService gallery, [EnumeratorCancellation] CancellationToken ct)
    {
        var userMessage = (body?.Message ?? "").Trim();
        if (userMessage.Length == 0)
        {
            // No real turn to run; surface a Meta with no id so the endpoint emits the in-band error + done.
            yield return StreamEvent.Meta("");
            yield break;
        }

        var card = Persona.Load(body!.Persona);   // null name / unknown => built-in default persona

        // A freshly-created thread picks up the global DEFAULT KB (no-op when none is set — byte-for-byte no-KB).
        var conv = ChatStore.Load(body.Conversation)
                   ?? ApplyDefaultKb(ChatStore.NewConversation(body.Persona, card?.Lorebook));

        IReadOnlyList<ChatTurn> history = SelectHistory(conv, body.Messages);
        var activeLore = ActivateLore(card?.Lorebook, history, userMessage);

        // P5 vision-in-chat (same rule as SendAsync): a resolvable gallery image attaches as multimodal content and
        // forces the Vision model/tier; an unresolvable/absent image streams text-only on the requested tier.
        var imageDataUrl = ResolveImage(body.Image, gallery);
        model = VisionModel(imageDataUrl, model);

        // KB context-injection (same as SendAsync): retrieve the conversation's relevant doc chunks before the
        // first token; a down embed server / no attached KB degrades to no injection (the no-KB stream unchanged).
        var activeDocs = await RetrieveDocs(conv.KbId, userMessage, ct).ConfigureAwait(false);
        var activeMemories = await MemoryRecall.RetrieveAsync(ct).ConfigureAwait(false);   // long-term [Memory] recall (global; gated on the store existing, degrades to empty)

        var messages = ChatPrompt.Build(card, history, userMessage, HistoryTurnBudget, activeLore, imageDataUrl, activeDocs, activeMemories);

        // Hand the conversation id AND its effective KbId to the endpoint up front (so the SPA can capture the id
        // before any token AND refresh _chatKbId for a fresh send that auto-attached the default KB — FIX 1).
        yield return StreamEvent.Meta(conv.Id, conv.KbId);

        var temperature = 0.8;
        var maxTokens = 1024;
        var acc = new System.Text.StringBuilder();
        await foreach (var delta in LocalLlm.ChatStreamAsync(messages, temperature, maxTokens, ct, model).ConfigureAwait(false))
        {
            acc.Append(delta);
            yield return StreamEvent.Token(delta);
        }

        var reply = acc.ToString().Trim();

        // Persist BOTH turns only when the stream COMPLETED NORMALLY: a non-empty reply AND no cancellation. On a
        // client abort the accumulation is PARTIAL, so storing it would make a reload show a turn that never
        // finished (and can diverge from the live view) — an aborted exchange persists neither turn. An empty
        // accumulation = LLM down (nothing useful to store; the endpoint surfaced the in-band error).
        if (reply.Length > 0 && !ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow.ToString("o");   // one timestamp for both turns (no microsecond drift)
            var appended = new List<ChatTurn>(conv.Messages)
            {
                new("user", userMessage, now),
                new("assistant", reply, now),
            };
            conv = conv with { Messages = appended };
            ChatStore.Save(conv);   // graceful: a save failure does not undo the reply the user already streamed
        }
    }

    // P5 acceptance crux, as ONE pure decision (shared by both send paths): when a multimodal image RESOLVED
    // (non-empty data URL), force the Vision model regardless of the requested speed tier (vision needs the VL
    // block); with no/unresolvable image, leave the requested model untouched (text-only on the asked tier).
    // Pure + total => unit-tested, so the "image => Vision" rule can't silently drift in just one of SendAsync/StreamAsync.
    internal static string? VisionModel(string? imageDataUrl, string? requestedModel)
        => string.IsNullOrEmpty(imageDataUrl) ? requestedModel : LlmTiers.Vision;

    // P5: resolve an optional gallery IMAGE NAME to a data: URL for a multimodal turn. Null/blank name, an unknown
    // name, or a non-image artifact => null (the turn proceeds text-only). Graceful — never throws: a bad name is
    // simply ignored, exactly like /api/describe rejects an unresolvable name. GalleryService.ImageDataUrl is
    // path-scoped (no traversal) and returns null for anything that isn't a real image in the gallery root.
    // AUDIT P2-4 (2026-07-01): takes the caller's GalleryService instance (the DI singleton in production) instead
    // of `new`-ing one, so this reuses the same instance StudioHost registers rather than splitting state.
    private static string? ResolveImage(string? imageName, GalleryService gallery)
    {
        if (string.IsNullOrWhiteSpace(imageName)) return null;
        try { return gallery.ImageDataUrl(imageName.Trim()); }
        catch { return null; }
    }

    // Pure history-source choice (no GPU/disk): the persisted transcript wins when the stored thread already has
    // turns; otherwise a stateless caller's supplied transcript seeds it; otherwise empty. Extracted from
    // SendAsync so this branching is unit-testable on its own.
    public static IReadOnlyList<ChatTurn> SelectHistory(Conversation conv, IReadOnlyList<ChatTurn>? supplied)
        => conv.Messages.Count > 0
            ? conv.Messages
            : (supplied ?? (IReadOnlyList<ChatTurn>)Array.Empty<ChatTurn>());

    // P3: load the card's lorebook (if any) and activate the [World Info] entries whose keys appear in the recent
    // transcript (the trimmed history turns' content + the new user message). Returns null when the card carries
    // no lorebook (so ChatPrompt.Build preserves the exact pre-P3 output) — behavior is identical for cards
    // without a lorebook. Graceful: a missing/unreadable lorebook simply yields no injection. Internal so the
    // no-lorebook short-circuit is unit-testable with no disk.
    internal static IReadOnlyList<LoreEntry>? ActivateLore(
        string? lorebookName, IReadOnlyList<ChatTurn> history, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(lorebookName)) return null;
        var book = Lorebook.Load(lorebookName);
        if (book?.Entries is not { Count: > 0 }) return null;

        // Scan EXACTLY the turns the prompt will send: the same recent-non-empty-within-budget window
        // ChatPrompt.Build uses (single source of truth), plus the new user message. Sharing the window means a
        // lore entry can never fire on a turn Build then trims away.
        var recent = ChatPrompt.RecentTurns(history, HistoryTurnBudget);
        var scanText = string.Join("\n", recent.Select(t => t.Content)) + "\n" + userMessage;

        return Lorebook.Activate(book.Entries, scanText, LoreMaxEntries, LoreMaxChars);
    }

    // KB context-injection: when the conversation has an attached KB (KbId set), retrieve the top-K doc chunks
    // most relevant to the LATEST user turn (semantic cosine over the kb_id-scoped doc_index rows) so
    // ChatPrompt.Build can inject a bounded [Documents] block — the deterministic, every-turn alternative to a
    // 5th chat tool. Returns null when there's no KB (so Build preserves the EXACT no-KB output) and DEGRADES to
    // null on any failure: a down :8090 embed server, a missing index, a sidecar error, or a timeout all yield
    // "no context injected" and plain chat proceeds unchanged — the SAME contract as code_search. Internal so the
    // no-KB short-circuit is unit-testable with no process.
    // PURE default-KB pickup (the v0.16 DEFAULT/GLOBAL KB follow-up): applied to a FRESHLY-created thread only —
    // the right-hand side of `ChatStore.Load(...) ?? ApplyDefaultKb(ChatStore.NewConversation(...))` at the three
    // send sites. When a global default is set (DefaultKbStore.Get() is non-null) a NEW thread auto-attaches to it
    // so the project docs ride in every fresh chat; when NO default is set, `fresh` is returned UNCHANGED (KbId
    // stays the record default null), so RetrieveDocs(null) short-circuits exactly as today — the no-default path
    // is byte-for-byte. A LOADED existing thread is NEVER passed here, so it is never re-pointed. Extracted (like
    // ResolveDetachKbId) so the "default applies to new only, null-default is a no-op" rule is unit-tested with no
    // live model. An already-attached `fresh` (KbId set) is left intact — the default never clobbers an explicit
    // choice.
    public static Conversation ApplyDefaultKb(Conversation fresh)
        => string.IsNullOrWhiteSpace(fresh.KbId) && DefaultKbStore.Get() is { } d
            ? fresh with { KbId = d }
            : fresh;

    internal static async Task<IReadOnlyList<DocChunk>?> RetrieveDocs(string? kbId, string userMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kbId) || string.IsNullOrWhiteSpace(userMessage)) return null;
        try
        {
            var docs = await DocSearch.RetrieveAsync(kbId, userMessage, ct).ConfigureAwait(false);
            return docs is { Count: > 0 } ? docs : null;   // empty => null so Build's no-KB output is byte-for-byte
        }
        catch { return null; }   // never let a KB hiccup break a reply
    }
}
