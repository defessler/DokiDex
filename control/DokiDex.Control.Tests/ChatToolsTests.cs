using System.Linq;
using System.Text.Json;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The curated tool registry for the agent loop (ONE tool this slice: search_library). Two seams kept pure +
// tested with NO GPU/disk: (a) the JSON schema object placed in the request 'tools' array is well-formed
// OpenAI function-tool shape, and (b) Run's arg-parse + unknown-tool fall-through. The search_library happy
// path itself touches the gallery on disk (GalleryService.List) and is exercised only thinly here — the
// fragile parsing is what's locked. Mirrors the small-curated-toolset / bounded-loop mitigation in decisions.md.
public class ChatToolsTests
{
    [Fact]
    public void The_tools_array_is_well_formed_openai_function_shape()
    {
        // Round-trip the schema the request will carry and assert the OpenAI tool contract:
        //   [{ type:"function", function:{ name, description, parameters:{ type:object, properties:{query} } } }]
        var json = JsonSerializer.Serialize(ChatTools.ToolsJson);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(3, root.GetArrayLength());   // gated 1 -> 3: search_library + web_search + code_search

        // EVERY tool is a well-formed OpenAI function schema with a {query:string} parameter.
        foreach (var tool in root.EnumerateArray())
        {
            Assert.Equal("function", tool.GetProperty("type").GetString());
            var fn = tool.GetProperty("function");
            Assert.False(string.IsNullOrWhiteSpace(fn.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fn.GetProperty("description").GetString()));
            var p = fn.GetProperty("parameters");
            Assert.Equal("object", p.GetProperty("type").GetString());
            Assert.True(p.GetProperty("properties").TryGetProperty("query", out var q));
            Assert.Equal("string", q.GetProperty("type").GetString());
        }

        var names = root.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "search_library", "web_search", "code_search" }, names);
    }

    [Fact]
    public void Run_with_an_unknown_tool_name_returns_a_clear_unknown_tool_message_listing_all_tools()
    {
        var result = ChatTools.Run("get_weather", "{\"city\":\"Tokyo\"}");
        Assert.Contains("unknown tool", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("get_weather", result);
        // The default text now lists the full curated set so the model can re-plan.
        Assert.Contains("search_library", result);
        Assert.Contains("web_search", result);
        Assert.Contains("code_search", result);
    }

    [Theory]
    [InlineData("{\"query\":\"x\",\"k\":3}", "x", 3)]
    [InlineData("{\"query\":\"x\"}", "x", 5)]                 // default k when omitted
    [InlineData("{\"query\":\"x\",\"k\":\"4\"}", "x", 4)]     // tolerant: a string-typed int
    [InlineData("not json", "", 5)]                            // malformed => empty query + default k
    public void ParseQueryAndK_extracts_query_and_optional_k(string args, string eq, int ek)
    {
        var (q, k) = ChatTools.ParseQueryAndK(args, 5);
        Assert.Equal(eq, q);
        Assert.Equal(ek, k);
    }

    // The sidecar-backed tools (web_search / code_search) used to be exercised by two Run-dispatch tests that
    // SPAWNED A REAL SUBPROCESS (uvx ddgs / uv run python ...): live network + a :8090 call on any box where the
    // tools are present — slow, flaky, and ZERO signal on the degrade/format path they were named for. They are
    // replaced here by HERMETIC coverage of the pure Result -> tool-text decision (ChatTools.FormatToolResult),
    // which is the exact guard logic Run runs after the (out-of-scope) exec returns. No process is spawned.

    [Fact]
    public void FormatToolResult_on_ok_but_empty_returns_the_clean_no_results_line_not_the_done_sentinel()
    {
        // FIX 2: a search that RAN FINE but matched nothing comes back Ok=true, Rows=[], Message="done". The
        // model must see the formatter's clean "no results" line — NOT the raw "done" sentinel. The success
        // branch ignores Message entirely and surfaces the formatted (empty => "no results") text.
        var formatted = WebSearch.FormatWebResults("nothing here", System.Array.Empty<(string, string, string)>());
        var text = ChatTools.FormatToolResult(ok: true, rowCount: 0, message: "done", formatted: formatted);

        Assert.Equal(formatted, text);
        Assert.Contains("no web results", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("done", text);   // the bare sentinel must never reach the model
    }

    [Fact]
    public void FormatToolResult_on_ok_with_rows_surfaces_the_formatted_results()
    {
        // Ok + rows => the formatted block verbatim (Message is irrelevant on the success path).
        var formatted = "2 web result(s) for \"x\": ...";
        var text = ChatTools.FormatToolResult(ok: true, rowCount: 2, message: "done", formatted: formatted);
        Assert.Equal(formatted, text);
    }

    [Fact]
    public void FormatToolResult_on_genuine_failure_surfaces_the_degrade_message_verbatim()
    {
        // Only a real !Ok (sidecar / embed server / network down) surfaces the specific degrade Message so the
        // model learns WHY the tool was unavailable (e.g. "start the embed server ..."), rather than a bland line.
        const string degrade = "code search unavailable — start the embed server (start-embed.ps1) and build the index (doki index).";
        var formatted = CodeSearch.FormatCodeResults("loop", System.Array.Empty<(string, int, int, string, double)>());
        var text = ChatTools.FormatToolResult(ok: false, rowCount: 0, message: degrade, formatted: formatted);
        Assert.Equal(degrade, text);
    }

    [Fact]
    public void FormatToolResult_on_failure_with_no_message_falls_back_to_the_clean_formatted_line()
    {
        // A !Ok with no specific message still degrades gracefully to the formatter's clean "no results" line
        // (never blank, never a throw) — the agent loop always gets usable tool text.
        var formatted = WebSearch.FormatWebResults("q", System.Array.Empty<(string, string, string)>());
        Assert.Equal(formatted, ChatTools.FormatToolResult(ok: false, rowCount: 0, message: null, formatted: formatted));
        Assert.Equal(formatted, ChatTools.FormatToolResult(ok: false, rowCount: 0, message: "   ", formatted: formatted));
    }

    [Theory]
    [InlineData("{\"query\":\"neon dragon\"}", "neon dragon")]
    [InlineData("{ \"query\" : \"  spaced  \" }", "spaced")]
    [InlineData("{\"other\":\"x\"}", "")]   // missing query => empty (a blank query lists everything)
    [InlineData("", "")]                    // no arguments at all => empty query, never throws
    [InlineData("not json", "")]            // malformed arguments => empty query, graceful
    public void ParseQuery_extracts_the_trimmed_query_or_empty(string argumentsJson, string expected)
        => Assert.Equal(expected, ChatTools.ParseQuery(argumentsJson));

    [Fact]
    public void Run_for_search_library_never_throws_and_returns_text()
    {
        // The disk call (GalleryService.List) is thin; with no gallery present it degrades to a clear "no
        // matching" text rather than throwing. Locks that Run is total even when the library is empty.
        var result = ChatTools.Run("search_library", "{\"query\":\"zzz-unlikely-match-zzz\"}");
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void A_long_item_prompt_is_truncated_so_the_tool_result_stays_bounded()
    {
        // B1: long SDXL prompts re-sent across up to MaxToolHops+1 hops would bloat context past max_tokens. Each
        // item's prompt is capped (first ~120 chars + an ellipsis) so the whole tool result stays small. The pure
        // formatter is exercised directly with a single very long prompt so the bound is deterministic (no disk).
        var longPrompt = new string('x', 5000);
        var text = ChatTools.FormatSearchResults("dragon", new[] { ("img_001.png", longPrompt) });

        // The full 5000-char prompt must NOT survive verbatim; the rendered prompt is capped to the per-item budget.
        Assert.DoesNotContain(longPrompt, text);
        Assert.True(text.Length < 400, $"tool result should stay small, was {text.Length} chars");
        Assert.Contains("img_001.png", text);   // the item is still listed, just with a truncated prompt
        Assert.Contains("…", text);             // the truncation marker is present
    }

    [Fact]
    public void A_short_item_prompt_is_left_intact()
    {
        // Below the cap, the prompt is shown verbatim with no truncation marker (the common case must be lossless).
        var text = ChatTools.FormatSearchResults("dragon", new[] { ("img_001.png", "a neon dragon at night") });
        Assert.Contains("a neon dragon at night", text);
        Assert.DoesNotContain("…", text);
    }
}
