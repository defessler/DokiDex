using System;
using System.Collections.Generic;

namespace DokiDex.Web;

// One chat-history search hit: the thread Id (so the SPA's existing openThread(id) opens it unchanged), a persona
// label (Conversation has no Title field, so Persona ?? "(default)" is the surfaced label, matching how
// loadThreads() labels options), and a single-line snippet around the first match.
public sealed record ChatHit(string Id, string? Persona, string Snippet);

// PURE chat-history match/snippet core: a literal, case-insensitive SUBSTRING scan over message CONTENT — NOT
// RAG/embedding. Total + side-effect-free over a hand-built thread list (unit-testable headless), so the
// case-insensitivity / first-hit snippet / newest-first / bound rules are locked here. The /api/chats/search
// endpoint is a thin Run(ChatStore.List(), q) over this; List() is already newest-first + skips malformed files,
// and no path is ever built from q, so there is zero traversal surface. Read-only, additive.
public static class ChatSearch
{
    // Bounds, all const so they're asserted in tests:
    public const int MaxResults = 50;       // stop after this many matching threads
    public const int SnippetRadius = 80;    // chars of context on each side of the match
    public const int MaxSnippetLen = 200;   // hard cap on the (pre-ellipsis) snippet window
    public const int MinQueryLen = 2;       // avoid a 1-char scan over everything

    public static IReadOnlyList<ChatHit> Run(IReadOnlyList<Conversation>? convs, string? q)
    {
        var query = q?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < MinQueryLen || convs is null)
            return Array.Empty<ChatHit>();

        var hits = new List<ChatHit>();
        foreach (var c in convs)
        {
            if (hits.Count >= MaxResults) break;
            if (c?.Messages is not { Count: > 0 } msgs) continue;

            // First hit wins: first turn in stored order whose Content contains the query (case-insensitive).
            foreach (var t in msgs)
            {
                var content = t?.Content;
                if (string.IsNullOrEmpty(content)) continue;
                var idx = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                hits.Add(new ChatHit(c.Id, string.IsNullOrWhiteSpace(c.Persona) ? "(default)" : c.Persona,
                    Snippet(content, idx, query.Length)));
                break;   // only the first matching turn is snippetted (O(content) per thread)
            }
        }
        return hits;
    }

    // Build a single-line snippet: SnippetRadius chars on each side of the match, collapse \r\n\t -> space,
    // prefix/suffix "…" when clipped, and hard-cap the window length. Pure + deterministic.
    private static string Snippet(string content, int matchIdx, int matchLen)
    {
        var start = Math.Max(0, matchIdx - SnippetRadius);
        var end = Math.Min(content.Length, matchIdx + matchLen + SnippetRadius);
        var window = content.Substring(start, end - start);

        // Collapse whitespace/newlines to single spaces so the snippet is one line.
        window = string.Join(" ", window.Split(
            new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));

        if (window.Length > MaxSnippetLen) window = window[..MaxSnippetLen];

        var prefix = start > 0 ? "…" : "";
        var suffix = end < content.Length ? "…" : "";
        return prefix + window + suffix;
    }
}
