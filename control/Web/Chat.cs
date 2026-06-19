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
public sealed record ChatRequest(
    string? Conversation, string? Persona, string? Message, string? Tier, IReadOnlyList<ChatTurn>? Messages = null);

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

    public sealed record Result(bool Ok, string ConversationId, string Text, string? Message);

    // One streamed event from StreamAsync. The FIRST event is always a Meta carrying the conversation id (so the
    // SPA can capture it before any token arrives); every subsequent event is a Delta carrying one token chunk.
    //   Meta(conversationId): IsMeta=true,  ConversationId set, Delta=null
    //   Token(delta):         IsMeta=false, Delta set
    public sealed record StreamEvent(bool IsMeta, string? ConversationId, string? Delta)
    {
        public static StreamEvent Meta(string id) => new(true, id, null);
        public static StreamEvent Token(string delta) => new(false, null, delta);
    }

    public static async Task<Result> SendAsync(ChatRequest body, string? model, CancellationToken ct)
    {
        var userMessage = (body?.Message ?? "").Trim();
        if (userMessage.Length == 0) return new Result(false, "", "", "empty message");

        var card = Persona.Load(body!.Persona);   // null name / unknown => built-in default persona

        // Load the existing thread, or start a new one (server-generated id => no client path => no traversal).
        var conv = ChatStore.Load(body.Conversation)
                   ?? ChatStore.NewConversation(body.Persona, card?.Lorebook);

        // Prior turns: the persisted transcript, or a stateless caller's supplied Messages when there's none.
        IReadOnlyList<ChatTurn> history = SelectHistory(conv, body.Messages);

        var activeLore = ActivateLore(card?.Lorebook, history, userMessage);
        var messages = ChatPrompt.Build(card, history, userMessage, HistoryTurnBudget, activeLore);

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

        return new Result(true, conv.Id, reply, null);
    }

    // STREAMING send (P2): the streaming twin of SendAsync. Loads the card + conversation (reusing SelectHistory
    // and the same empty-message guard), assembles the prompt via the pure ChatPrompt.Build, then streams the
    // upstream content deltas to the caller WHILE accumulating the full reply; when the stream completes it
    // persists BOTH turns (user + accumulated assistant) exactly like SendAsync. The FIRST yielded event is a
    // Meta carrying the (server-generated) conversation id so the endpoint can frame it before any token; each
    // later event is one token Delta. Zero deltas (LLM down / no model) => the caller emits the in-band error.
    public static async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatRequest body, string? model, [EnumeratorCancellation] CancellationToken ct)
    {
        var userMessage = (body?.Message ?? "").Trim();
        if (userMessage.Length == 0)
        {
            // No real turn to run; surface a Meta with no id so the endpoint emits the in-band error + done.
            yield return StreamEvent.Meta("");
            yield break;
        }

        var card = Persona.Load(body!.Persona);   // null name / unknown => built-in default persona

        var conv = ChatStore.Load(body.Conversation)
                   ?? ChatStore.NewConversation(body.Persona, card?.Lorebook);

        IReadOnlyList<ChatTurn> history = SelectHistory(conv, body.Messages);
        var activeLore = ActivateLore(card?.Lorebook, history, userMessage);
        var messages = ChatPrompt.Build(card, history, userMessage, HistoryTurnBudget, activeLore);

        // Hand the conversation id to the endpoint up front (so the SPA can capture it before any token).
        yield return StreamEvent.Meta(conv.Id);

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
}
