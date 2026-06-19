using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DokiDex.Web;

// The curated, in-process TOOL REGISTRY for the chat agent loop. This slice ships THREE tools — search_library
// (over the existing GalleryService), web_search (DuckDuckGo via the `uvx ddgs` sidecar), and code_search
// (semantic RAG over this repo via the code_index.py `search` dispatch over the :8090 embed server). The
// small-curated-set discipline still holds at THREE: decisions.md records that open models lose tool-selection
// accuracy as the tool list grows, so the mitigation is a SMALL curated set + a BOUNDED loop (Chat.MaxToolHops)
// + graceful fallthrough (a plain-content reply with no tool_calls IS the answer). The sidecar tools degrade
// (never throw, never hang) when uvx/uv/the index/the embed server are absent. Further tools stay FUTURE gated
// additions — generate-from-chat / a GPU handoff are out of this slice.
//
// Surfaces, kept pure where possible (no GPU; the sidecar/disk touch is thin and degrades):
//   • ToolsJson    — the OpenAI 'tools' array placed verbatim into the request body (well-formed, unit-tested).
//   • Run(name, argumentsJson) — dispatch by name; an unknown name returns a clear text (never throws) so the
//     model gets a usable tool result and the loop can recover. ParseQuery / ParseQueryAndK are the pure arg-parse
//     seams; FormatToolResult is the pure Result -> tool-text decision shared by the two sidecar executors.
public static class ChatTools
{
    // How many top library matches a search_library result folds in (name + prompt each), to bound the tool text.
    private const int MaxResults = 8;

    // Per-item prompt cap (chars). Long SDXL prompts are re-sent on EVERY hop of the agent loop, so an untruncated
    // prompt × MaxResults × hops bloats context past max_tokens. Each item's prompt is clipped to this many chars
    // (+ an ellipsis) so the whole tool result stays small while the file name + a readable preview survive.
    private const int MaxPromptChars = 120;

    // The single function-tool schema, in the OpenAI tool shape coder-fast accepts (proven by
    // serving/test-toolcall.ps1): { type:"function", function:{ name, description, parameters{...} } }.
    public static readonly object SearchLibrarySchema = new
    {
        type = "function",
        function = new
        {
            name = "search_library",
            description = "Search the user's local media library (generated images, video, and audio) by a "
                + "free-text query that matches anywhere in each item's prompt. Returns the top matching items' "
                + "file names and prompts. Use this to find, reference, or reason about what the user has already "
                + "generated. An empty query lists the most recent items.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new
                    {
                        type = "string",
                        description = "Free-text terms to match against item prompts (e.g. 'neon dragon at night'). "
                            + "Leave empty to list the most recent items.",
                    },
                },
                required = Array.Empty<string>(),   // query is optional: an empty query lists recent items
            },
        },
    };

    // web_search — DuckDuckGo via the ddgs CLI (sidecar). Sharp description: this is for CURRENT/EXTERNAL facts
    // the model can't know, NOT the user's local library. A {query} + an optional {k} (result count).
    public static readonly object WebSearchSchema = new
    {
        type = "function",
        function = new
        {
            name = "web_search",
            description = "Search the public web (DuckDuckGo) for current or external information the model "
                + "doesn't already know — news, docs, facts, prices, definitions. Returns the top result titles, "
                + "URLs, and snippets. Use this for anything NOT in the user's local media library or this repo's "
                + "code (use search_library / code_search for those).",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "The web search query (e.g. 'latest SDXL turbo release notes')." },
                    k = new { type = "integer", description = "How many results to return (1-10, default 5)." },
                },
                required = new[] { "query" },
            },
        },
    };

    // code_search — semantic RAG over THIS repo's indexed source (sidecar). Sharp description: this is for
    // WHERE-is-it-in-the-code questions where a literal grep would miss the right file.
    public static readonly object CodeSearchSchema = new
    {
        type = "function",
        function = new
        {
            name = "code_search",
            description = "Semantic search over THIS project's source code (RAG) to find WHERE something is "
                + "implemented when a literal keyword search would miss the right file (different wording, related "
                + "concept). Returns the most relevant code chunks with file path and line range. Requires the "
                + "local code index; if unavailable it says so.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "What to find in the codebase (e.g. 'the bounded agent tool loop')." },
                    k = new { type = "integer", description = "How many code chunks to return (1-10, default 5)." },
                },
                required = new[] { "query" },
            },
        },
    };

    // The 'tools' array placed into the request body. A deliberate gated expansion 1 -> 3 (search_library +
    // web_search + code_search); decisions.md warns open models lose tool-selection accuracy as the list grows,
    // so the set stays SMALL and the descriptions stay sharp. Do not sprawl past this curated trio.
    public static readonly object[] ToolsJson = { SearchLibrarySchema, WebSearchSchema, CodeSearchSchema };

    // PURE: pull the trimmed {query} string out of an OpenAI tool-call arguments JSON STRING. A missing/blank
    // query, no arguments at all, or malformed JSON all yield "" (a blank query lists the most recent items) —
    // never throws, so a sloppy model argument degrades to a harmless broad search. Total + side-effect-free.
    public static string ParseQuery(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("query", out var q)
                && q.ValueKind == JsonValueKind.String)
                return (q.GetString() ?? "").Trim();
            return "";
        }
        catch { return ""; }
    }

    // PURE: pull a {query} string AND an optional integer {k} out of a tool-call arguments JSON STRING, for tools
    // that take a result-count. Reuses ParseQuery for the query; k defaults to `defaultK` when absent, and is
    // tolerant of a string-typed int (some models emit "k":"4"). Malformed/missing => ("" , defaultK). Never
    // throws. The executor clamps k to a sane range, so an out-of-band number is harmless.
    public static (string query, int k) ParseQueryAndK(string? argumentsJson, int defaultK)
    {
        var query = ParseQuery(argumentsJson);
        var k = defaultK;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("k", out var kv))
                {
                    if (kv.ValueKind == JsonValueKind.Number && kv.TryGetInt32(out var ki)) k = ki;
                    else if (kv.ValueKind == JsonValueKind.String && int.TryParse(kv.GetString(), out var ks)) k = ks;
                }
            }
            catch { /* keep defaultK */ }
        }
        return (query, k);
    }

    // Dispatch a tool by name, returning the tool RESULT TEXT that becomes the role:"tool" message content. An
    // unknown tool name yields a clear "unknown tool" text (the model can recover / re-plan) rather than throwing,
    // and the search_library executor is wrapped so a disk hiccup degrades to a graceful message — the agent loop
    // must never crash on a tool. Names are matched case-insensitively, exact.
    public static string Run(string? name, string? argumentsJson)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "search_library":
                return RunSearchLibrary(ParseQuery(argumentsJson));
            case "web_search":
            {
                var (q, k) = ParseQueryAndK(argumentsJson, 5);
                return RunWebSearch(q, k);
            }
            case "code_search":
            {
                var (q, k) = ParseQueryAndK(argumentsJson, 5);
                return RunCodeSearch(q, k);
            }
            default:
                return $"unknown tool: '{name}'. Available tools are: search_library, web_search, code_search.";
        }
    }

    // The one thin disk call: query the gallery and fold the top matches into a compact text block (file name +
    // its prompt). Kept minimal and graceful so the pure ParseQuery + unknown-tool paths carry the test weight.
    private static string RunSearchLibrary(string query)
    {
        try
        {
            var items = new GalleryService().List(query).Take(MaxResults).ToList();
            if (items.Count == 0)
                return string.IsNullOrEmpty(query)
                    ? "The library is empty — nothing has been generated yet."
                    : $"No library items match \"{query}\".";

            return FormatSearchResults(query, items.Select(NameAndPrompt).ToList());
        }
        catch (Exception ex)
        {
            return $"search_library failed: {ex.Message}";
        }
    }

    // web_search executor: shell the ddgs sidecar (bounded by its own 20s timeout), then render the top results
    // to a SHORT bounded text. Run is synchronous (Chat.cs calls it sync), so we block on the async sidecar with
    // GetAwaiter().GetResult(); the Result -> tool-text decision goes through the pure FormatToolResult so an
    // Ok-but-empty search yields the formatter's clean "no web results" line (NOT the "done" sentinel) and only a
    // genuine !Ok surfaces the degrade message. Any unexpected throw is caught — the agent loop must never crash
    // on a tool. Result text is bounded so it can't bloat context across the loop's hops.
    private static string RunWebSearch(string query, int k)
    {
        try
        {
            var r = WebSearch.SearchAsync(query, k, CancellationToken.None).GetAwaiter().GetResult();
            return FormatToolResult(r.Ok, r.Rows.Count, r.Message, WebSearch.FormatWebResults(query, r.Rows));
        }
        catch (Exception ex)
        {
            return $"web_search unavailable: {ex.Message}";
        }
    }

    // code_search executor: shell the code_index.py search sidecar (bounded 30s), render the top chunks to a
    // SHORT bounded text. Same sync-blocking + graceful-degrade contract as web_search, via the same pure
    // FormatToolResult decision: an Ok-but-empty search renders the formatter's "no matching code" line (NOT the
    // raw "done" sentinel), and only a down embed server / unbuilt index (!Ok) surfaces its specific message.
    private static string RunCodeSearch(string query, int k)
    {
        try
        {
            var r = CodeSearch.SearchAsync(query, k, CancellationToken.None).GetAwaiter().GetResult();
            return FormatToolResult(r.Ok, r.Rows.Count, r.Message, CodeSearch.FormatCodeResults(query, r.Rows));
        }
        catch (Exception ex)
        {
            return $"code_search unavailable: {ex.Message}";
        }
    }

    // PURE: the sidecar Result -> tool-text decision shared by RunWebSearch / RunCodeSearch (so it can be
    // unit-tested with NO process). `formatted` is the bounded formatter output — the results block when rowCount
    // > 0, else the formatter's clean "no results / unavailable" line.
    //   • ok  -> `formatted` ALWAYS. On a ran-fine-but-empty search (rowCount == 0) the sidecars carry Message
    //            "done"; the success branch must IGNORE that sentinel and surface the clean "no results" line.
    //   • !ok -> a genuine degrade (sidecar / embed server / network down): surface the SPECIFIC `message` so the
    //            model learns why, falling back to `formatted` (the clean line) when no message was provided.
    // Total + side-effect-free, so the "done must not leak" guard can't silently regress.
    public static string FormatToolResult(bool ok, int rowCount, string? message, string formatted)
    {
        _ = rowCount;   // reserved for future per-state shaping; the decision is ok vs !ok today
        if (ok) return formatted;
        return string.IsNullOrWhiteSpace(message) ? formatted : message!;
    }

    // PURE: render the matched (name, prompt) pairs into the compact tool-result text, TRUNCATING each prompt to
    // MaxPromptChars so the whole block stays small across the agent loop's repeated hops. The header reflects
    // whether the query was a free-text search or the empty "most recent" listing. Side-effect-free + total =>
    // unit-tested, so the per-item bound can't silently drift. (Callers pass a non-empty list; an empty list still
    // renders a header-only line harmlessly.)
    public static string FormatSearchResults(string query, IReadOnlyList<(string name, string prompt)> items)
    {
        var sb = new StringBuilder();
        sb.Append(items.Count).Append(string.IsNullOrEmpty(query)
            ? " most recent library item(s):" : $" library item(s) matching \"{query}\":");
        foreach (var (n, p) in items)
        {
            sb.Append("\n- ").Append(n);
            var preview = Truncate(p, MaxPromptChars);
            if (!string.IsNullOrWhiteSpace(preview)) sb.Append(" — ").Append(preview);
        }
        return sb.ToString();
    }

    // Clip a prompt to at most `cap` chars, appending an ellipsis when clipped. Null/short prompts pass through
    // unchanged (no marker) so the common case is lossless.
    private static string Truncate(string? s, int cap)
    {
        s ??= "";
        return s.Length <= cap ? s : s[..cap] + "…";
    }

    // GalleryService.List returns an anonymous-object projection; pull name + prompt by round-tripping through
    // JSON (the projection's shape is { name, kind, prompt, ... }). Robust to a missing field => "".
    private static (string name, string prompt) NameAndPrompt(object dto)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto));
            var r = doc.RootElement;
            var name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            var prompt = r.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            return (name, prompt);
        }
        catch { return ("", ""); }
    }
}
