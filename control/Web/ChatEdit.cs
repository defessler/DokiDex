using System;
using System.Collections.Generic;

namespace DokiDex.Web;

// PURE thread-mutation cores for chat interactivity (regenerate / edit / delete). Each takes the conversation
// transcript (IReadOnlyList<ChatTurn>) + an index and returns a NEW list — NO disk, NO GPU, NO LLM — so they
// unit-test exactly like ChatPrompt / ChatSearch / SelectHistory. Index == the 0-based position in
// conv.Messages, matching the SPA's _chatMsgs index (renderThread maps 1:1). All three are TOTAL: an
// out-of-range index returns the INPUT list unchanged (the same reference), which the endpoints map to a graceful
// no-op (never a crash). They are the side-effect-free primitives the thin Save-only / truncate-then-resend
// endpoints build on; the LLM re-run reuses the EXISTING Chat send/stream path byte-for-byte (no duplication).
public static class ChatEdit
{
    // Keep thread[0..index) — everything STRICTLY BEFORE index — dropping index and all turns after it. This is
    // the primitive the other two build on. index == 0 yields empty; index == Count is a no-op (nothing after).
    // An out-of-range index (index < 0 or index > Count) returns the input UNCHANGED (graceful, not a throw).
    public static IReadOnlyList<ChatTurn> TruncateToTurn(IReadOnlyList<ChatTurn> thread, int index)
    {
        if (thread is null) return Array.Empty<ChatTurn>();
        if (index < 0 || index > thread.Count) return thread;   // out-of-range => unchanged
        var kept = new List<ChatTurn>(index);
        for (var i = 0; i < index; i++) kept.Add(thread[i]);
        return kept;
    }

    // Replace a USER turn's content, then DROP everything after it (the stale assistant reply + any later turns
    // are invalid once the question changed). Equivalent to: replace thread[index].Content, then
    // TruncateToTurn(result, index+1). Valid ONLY when index is in range AND thread[index].Role == "user";
    // otherwise the input is returned UNCHANGED (editing an assistant turn is rejected — out of scope for a
    // resend). The user turn's timestamp is preserved (only the content changed).
    public static IReadOnlyList<ChatTurn> EditTurn(IReadOnlyList<ChatTurn> thread, int index, string newContent)
    {
        if (thread is null) return Array.Empty<ChatTurn>();
        if (index < 0 || index >= thread.Count) return thread;
        if (!IsUser(thread[index])) return thread;   // only a user turn can be edited-and-resent

        var kept = new List<ChatTurn>(index + 1);
        for (var i = 0; i < index; i++) kept.Add(thread[i]);
        kept.Add(new ChatTurn("user", newContent ?? "", thread[index].Ts));
        return kept;
    }

    // Remove a turn with the PAIRING RULE: a USER turn OWNS its following assistant reply — when thread[index] is
    // a user turn AND thread[index+1] exists and is an assistant turn, BOTH are dropped. An ASSISTANT turn drops
    // ONLY itself (leaving the user turn that can then be regenerated). A user turn NOT followed by an assistant
    // (the trailing un-answered turn, or a user-then-user transcript) drops only itself. Out-of-range => the
    // input is returned UNCHANGED. Bounds are checked before indexing index+1, so this is total.
    public static IReadOnlyList<ChatTurn> DeleteTurn(IReadOnlyList<ChatTurn> thread, int index)
    {
        if (thread is null) return Array.Empty<ChatTurn>();
        if (index < 0 || index >= thread.Count) return thread;

        var dropPair = IsUser(thread[index])
                       && index + 1 < thread.Count
                       && IsAssistant(thread[index + 1]);

        var result = new List<ChatTurn>(thread.Count - (dropPair ? 2 : 1));
        for (var i = 0; i < thread.Count; i++)
        {
            if (i == index) continue;
            if (dropPair && i == index + 1) continue;
            result.Add(thread[i]);
        }
        return result;
    }

    // The BRANCH prefix: keep turns [0..index] — everything UP TO AND INCLUDING the chosen turn — for a
    // non-destructive fork. Reuses TruncateToTurn with index+1 (TruncateToTurn keeps STRICTLY before its arg, so
    // index+1 keeps through index). Out-of-range is graceful: index >= Count keeps the WHOLE thread (a full-thread
    // fork is the safe default), index < 0 yields empty. The source list is never mutated.
    //   Defensive guard: when index >= thread.Count we keep the whole thread DIRECTLY rather than computing
    //   index+1 — at index == int.MaxValue that addition would overflow to int.MinValue and TruncateToTurn would
    //   return EMPTY (the opposite of the documented full-thread fork). Bailing on index >= Count first makes the
    //   helper total for every int, matching the doc (unreachable via the endpoint, whose index>=Count 400-guard
    //   rejects it first, but the public primitive must hold on its own).
    public static IReadOnlyList<ChatTurn> BranchAtTurn(IReadOnlyList<ChatTurn> thread, int index)
    {
        if (thread is null) return Array.Empty<ChatTurn>();
        if (index >= thread.Count) return thread;   // full-thread fork (also avoids the index+1 overflow)
        return TruncateToTurn(thread, index + 1);
    }

    // The regenerate per-resend OVERRIDE normalizer (PURE, no disk/GPU): a single field of the optional
    // ChatRegenerateRequest body (persona OR tier) is reduced to a clean override-or-null. A null / empty /
    // whitespace value => null => "no override" => the resend keeps the thread's STORED persona + the default
    // tier (the v0.22 behavior, byte-for-byte); a real value is trimmed and passed through as the override.
    // Extracted from the inline endpoint logic (was `IsNullOrWhiteSpace(x) ? null : x.Trim()` per field) so the
    // resend's Persona/Tier-resolution contract is explicit + unit-tested, exactly as BranchAtTurn was.
    public static string? NormalizeOverride(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    // The regenerate ANCHOR: the index of the LAST user turn (scan from the end for Role == "user"), or -1 when
    // the thread has no user turn. Regenerate-last truncates the persisted thread to THIS index (dropping the
    // last user turn AND its assistant reply) and resends with that user turn's content as the new userMessage —
    // so the existing send/append path re-adds exactly one user + one assistant turn (a fresh reply).
    public static int LastUserTurnIndex(IReadOnlyList<ChatTurn> thread)
    {
        if (thread is null) return -1;
        for (var i = thread.Count - 1; i >= 0; i--)
            if (IsUser(thread[i])) return i;
        return -1;
    }

    private static bool IsUser(ChatTurn t)
        => t is not null && string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssistant(ChatTurn t)
        => t is not null && string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase);
}
