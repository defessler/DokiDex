using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DokiDex.Web;

// The curated, in-process TOOL REGISTRY for the chat agent loop. This slice ships FOUR tools — three READ tools:
// search_library (over the existing GalleryService), web_search (DuckDuckGo via the `uvx ddgs` sidecar), and
// code_search (semantic RAG over this repo via the code_index.py `search` dispatch over the :8090 embed server);
// plus the lone WRITE/queue tool, generate_image, which QUEUES an image gen to PendingGenStore (GPU-arbitration-safe:
// it never renders mid-chat and never evicts the resident chat LLM). The small-curated-set discipline still holds
// at FOUR: decisions.md records that open models lose tool-selection accuracy as the tool list grows, so the
// mitigation is a SMALL curated set + a BOUNDED loop (Chat.MaxToolHops) + graceful fallthrough (a plain-content
// reply with no tool_calls IS the answer). The sidecar tools degrade (never throw, never hang) when uvx/uv/the
// index/the embed server are absent. Further tools stay FUTURE gated additions — do not sprawl past this curated set.
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

    // generate_image — the lone WRITE/ACT tool among three READ tools. It QUEUES an image gen; it does NOT render
    // now (chat runs with the LLM resident; SwarmUI is GPU-exclusive with it on 32GB — decisions.md). The sharp,
    // disjoint description is the defense against the tool-selection accuracy cost of a 4th tool: it states the
    // ACTION verb + queue semantics up front, the NEGATIVE boundary (don't pick it to FIND existing media — that's
    // search_library), and the deferred-render truth (render happens after the user switches to Media mode). Params
    // map 1:1 onto GenRequest(Prompt, Kind, Model, Count): a required prompt + optional kind/model/count.
    public static readonly object GenerateImageSchema = new
    {
        type = "function",
        function = new
        {
            name = "generate_image",
            description = "Queue an image to be generated from a text prompt. Use this to CREATE a NEW image, NOT "
                + "to find existing ones (use search_library for the user's existing media). This QUEUES the "
                + "request; rendering happens after the user switches to Media mode — the GPU can't run the image "
                + "model while we're chatting, so the tool never renders right now, it queues for later.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    prompt = new { type = "string", description = "The image description to generate (e.g. 'a neon dragon over a rainy city at night')." },
                    kind = new { type = "string", description = "Image family only: 'image' (default) or 'edit'. Video/music are not supported here." },
                    model = new { type = "string", description = "Checkpoint name, or 'auto' to route by prompt. Omit for the recipe default." },
                    count = new { type = "integer", description = "How many images to queue (1-9, default 1)." },
                },
                required = new[] { "prompt" },
            },
        },
    };

    // The 'tools' array placed into the request body. A deliberate gated expansion 1 -> 4 (search_library +
    // web_search + code_search + generate_image); decisions.md warns open models lose tool-selection accuracy as
    // the list grows, so the set stays SMALL and the descriptions stay sharp. generate_image is acceptable as the
    // 4th BECAUSE it is the lone write-action among three read-tools and its description is crisply non-overlapping.
    // Do not sprawl past this curated set.
    public static readonly object[] ToolsJson = { SearchLibrarySchema, WebSearchSchema, CodeSearchSchema, GenerateImageSchema };

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

    // PURE: map a generate_image tool-call arguments JSON STRING onto the GenRequest-shaped fields the pending-gen
    // store persists — (prompt, kind, model, count). Only the COUNT CLAMP is shared with the web /generate endpoint
    // (StudioHost.cs); the kind-narrowing and model-null below are deliberately gen-from-chat-specific (and arguably
    // safer) rules — /generate validates kind across the full Kinds set {image,video,music,edit,i2v,foley} and takes
    // model as-is, so do NOT widen these to match it:
    //   • prompt — trimmed; blank/missing/malformed => "" (the executor turns "" into the "need a prompt" line).
    //   • kind   — lower-cased; restricted to the image family {image, edit}; anything else (incl. video/music/i2v
    //              /foley/missing) => "image". Gen-from-chat is image-only because video/music need init images /
    //              other controls. (/generate instead 400s an unknown kind across the full Kinds set.)
    //   • model  — trimmed; blank/missing => null (recipe default). "auto" passes through to be routed at submit.
    //              (/generate takes model as-is.)
    //   • count  — Math.Clamp(.., 1, 9): the ONE rule shared with /generate; tolerant of a string-typed int; missing => 1.
    // Total + side-effect-free, never throws — a sloppy model argument degrades to safe defaults. The unit-test seam.
    public static (string prompt, string kind, string? model, int count) MapGenArgs(string? argumentsJson)
    {
        var prompt = "";
        var kind = "image";
        string? model = null;
        var count = 1;
        if (string.IsNullOrWhiteSpace(argumentsJson)) return (prompt, kind, model, count);
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (prompt, kind, model, count);

            if (root.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
                prompt = (p.GetString() ?? "").Trim();

            if (root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
            {
                var kv = (k.GetString() ?? "").Trim().ToLowerInvariant();
                if (kv is "image" or "edit") kind = kv;   // image family only; everything else falls back to "image"
            }

            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var mv = (m.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(mv)) model = mv;
            }

            if (root.TryGetProperty("count", out var c))
            {
                if (c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci)) count = ci;
                else if (c.ValueKind == JsonValueKind.String && int.TryParse(c.GetString(), out var cs)) count = cs;
            }
        }
        catch { /* keep safe defaults */ }
        count = Math.Clamp(count, 1, 9);
        return (prompt, kind, model, count);
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
            case "generate_image":
            {
                var (prompt, kind, model, count) = MapGenArgs(argumentsJson);
                return RunGenerateImage(prompt, kind, model, count);
            }
            default:
                return $"unknown tool: '{name}'. Available tools are: search_library, web_search, code_search, generate_image.";
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

    // generate_image executor: QUEUE-AND-NOTIFY. It is GPU-arbitration-safe by construction — it NEVER switches the
    // GPU mode or evicts the resident chat LLM (that stays a deliberate, user-confirmed action via the Media
    // composer's ensureMedia() guard). A blank prompt returns the "need a prompt" line WITHOUT touching disk. Else
    // it persists a durable pending-gen (survives the eventual GPU flip) and returns the bounded queued notice. The
    // single disk touch (PendingGenStore.Enqueue) is wrapped so the agent loop never crashes on the tool; the store
    // itself already degrades gracefully, so on any failure we still return an honest "queued" notice.
    private static string RunGenerateImage(string prompt, string kind, string? model, int count)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "I need a prompt describing the image to generate. Tell me what to create and I'll queue it.";
        try { PendingGenStore.Enqueue(prompt, kind, model, count, conversation: null); }
        catch { /* the agent loop must never crash on a tool; the notice below is still honest */ }
        return FormatGenQueued(count, kind);
    }

    // PURE: the bounded queued-gen notice. States the count + kind and that rendering needs a Media-mode switch
    // (the GPU can't run the image model during chat). Side-effect-free + total => unit-tested; bounded so it can't
    // bloat context across the loop's hops. No ids leak.
    public static string FormatGenQueued(int count, string kind)
        => $"Queued {count} {kind}(s) for generation. Switch to Media mode to render them "
            + "(the GPU can't run the image model while we're chatting).";

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
