using System.Collections.Generic;
using System.Linq;

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

    public sealed record Result(bool Ok, string ConversationId, string Text, string? Message);

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

        var messages = ChatPrompt.Build(card, history, userMessage, HistoryTurnBudget);

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

    // Pure history-source choice (no GPU/disk): the persisted transcript wins when the stored thread already has
    // turns; otherwise a stateless caller's supplied transcript seeds it; otherwise empty. Extracted from
    // SendAsync so this branching is unit-testable on its own.
    public static IReadOnlyList<ChatTurn> SelectHistory(Conversation conv, IReadOnlyList<ChatTurn>? supplied)
        => conv.Messages.Count > 0
            ? conv.Messages
            : (supplied ?? (IReadOnlyList<ChatTurn>)Array.Empty<ChatTurn>());
}
