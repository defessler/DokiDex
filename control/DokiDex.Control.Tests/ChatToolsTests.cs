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
        Assert.Equal(1, root.GetArrayLength());   // exactly ONE curated tool this slice

        var tool = root[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var fn = tool.GetProperty("function");
        Assert.Equal("search_library", fn.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(fn.GetProperty("description").GetString()));

        var p = fn.GetProperty("parameters");
        Assert.Equal("object", p.GetProperty("type").GetString());
        Assert.True(p.GetProperty("properties").TryGetProperty("query", out var q));
        Assert.Equal("string", q.GetProperty("type").GetString());
    }

    [Fact]
    public void Run_with_an_unknown_tool_name_returns_a_clear_unknown_tool_message()
    {
        var result = ChatTools.Run("get_weather", "{\"city\":\"Tokyo\"}");
        Assert.Contains("unknown tool", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("get_weather", result);
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
