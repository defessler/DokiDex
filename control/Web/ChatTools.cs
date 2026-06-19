using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DokiDex.Web;

// The curated, in-process TOOL REGISTRY for the chat agent loop. This slice ships exactly ONE tool —
// search_library — over the existing GalleryService. The single-tool scope is deliberate: decisions.md records
// that open models lose tool-selection accuracy as the tool list grows, so the mitigation is a SMALL curated set
// + a BOUNDED loop (Chat.MaxToolHops) + graceful fallthrough (a plain-content reply with no tool_calls IS the
// answer). More tools are FUTURE gated additions — web-search / code-RAG / generate-from-chat need external
// integration or a GPU handoff and are out of this slice.
//
// Two surfaces, kept pure where possible (no GPU; the disk touch is thin):
//   • ToolsJson    — the OpenAI 'tools' array placed verbatim into the request body (well-formed, unit-tested).
//   • Run(name, argumentsJson) — dispatch by name; an unknown name returns a clear text (never throws) so the
//     model gets a usable tool result and the loop can recover. ParseQuery is the pure arg-parse seam.
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

    // The 'tools' array placed into the request body. ONE tool this slice (a curated set is the open-model
    // tool-selection mitigation); future tools append here as gated additions.
    public static readonly object[] ToolsJson = { SearchLibrarySchema };

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
            default:
                return $"unknown tool: '{name}'. The only available tool is search_library.";
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
