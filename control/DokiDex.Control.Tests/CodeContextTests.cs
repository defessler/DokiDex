using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// 1.3 — context accounting: the PURE seams behind the meter, /compact, and /context. EstimateTokens/FormatK/
// IsSystemMessage/SelectForCompaction/RenderTranscript are total + side-effect-free (no disk, no model, no
// network) so they're locked down here with no live LLM. CompactAsync's one impure LLM round-trip is covered only
// for its network-free short-circuit (nothing to compact); the LLM-unreachable failure path is verified
// empirically (see the leaf's report) since LocalLlm is a static client with no clean seam to mock.
public class CodeContextTests
{
    private static object Sys(string content) => new { role = "system", content };
    private static object Usr(string content) => new { role = "user", content };
    private static object Asst(string content) => new { role = "assistant", content };
    private static object ToolMsg(string name, string content) => new { role = "tool", tool_call_id = "call_0", name, content };

    // ---- EstimateTokens ----

    [Fact]
    public void EstimateTokens_of_empty_list_is_zero()
        => Assert.Equal(0, CodeContext.EstimateTokens(Array.Empty<object>()));

    [Fact]
    public void EstimateTokens_sums_each_messages_own_serialized_length_over_4()
    {
        var msgs = new object[] { Usr("hello"), Asst("world") };
        var expected = msgs.Sum(m => System.Text.Json.JsonSerializer.Serialize(m).Length / 4);
        Assert.Equal(expected, CodeContext.EstimateTokens(msgs));
        Assert.True(CodeContext.EstimateTokens(msgs) > 0);
    }

    [Fact]
    public void EstimateTokens_grows_with_more_or_longer_messages()
    {
        var small = new object[] { Usr("hi") };
        var big = new object[] { Usr(new string('x', 5000)) };
        Assert.True(CodeContext.EstimateTokens(big) > CodeContext.EstimateTokens(small));

        var one = new object[] { Usr("hi") };
        var two = new object[] { Usr("hi"), Usr("hi") };
        Assert.True(CodeContext.EstimateTokens(two) > CodeContext.EstimateTokens(one));
    }

    // ---- FormatK ----

    [Theory]
    [InlineData(0, "0.0k")]
    [InlineData(500, "0.5k")]
    [InlineData(12345, "12.3k")]
    [InlineData(32000, "32.0k")]
    public void FormatK_renders_one_decimal_k_suffix(int tokens, string expected)
        => Assert.Equal(expected, CodeContext.FormatK(tokens));

    [Fact]
    public void FormatK_is_culture_invariant()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");   // uses ',' as decimal separator
            Assert.Equal("12.3k", CodeContext.FormatK(12345));
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    // ---- IsSystemMessage ----

    [Fact]
    public void IsSystemMessage_flags_only_role_system()
    {
        Assert.True(CodeContext.IsSystemMessage(Sys("x")));
        Assert.False(CodeContext.IsSystemMessage(Usr("x")));
        Assert.False(CodeContext.IsSystemMessage(Asst("x")));
        Assert.False(CodeContext.IsSystemMessage(ToolMsg("Read", "x")));
    }

    // ---- SelectForCompaction ----

    [Fact]
    public void SelectForCompaction_with_fewer_than_keepLastTurns_non_system_turns_compacts_nothing()
    {
        var working = new List<object> { Sys("prompt"), Usr("a"), Asst("b") };   // only 2 non-system turns
        var (toSummarize, kept) = CodeContext.SelectForCompaction(working, keepLastTurns: 4);
        Assert.Empty(toSummarize);
        Assert.Equal(working, kept);
    }

    [Fact]
    public void SelectForCompaction_keeps_all_leading_system_messages_and_last_N_non_system()
    {
        var working = new List<object>
        {
            Sys("prompt"), Sys("orientation"),          // two leading system messages (1.2)
            Usr("1"), Asst("2"), Usr("3"), Asst("4"), Usr("5"), Asst("6"),   // 6 non-system turns
        };
        var (toSummarize, kept) = CodeContext.SelectForCompaction(working, keepLastTurns: 4);

        Assert.Equal(2, toSummarize.Count);   // "1","2" go to the summarizer
        Assert.Equal(6, kept.Count);          // 2 system + last 4 non-system
        Assert.True(CodeContext.IsSystemMessage(kept[0]));
        Assert.True(CodeContext.IsSystemMessage(kept[1]));
        // last 4 non-system turns survive, in original order
        Assert.Equal(new object[] { Usr("3"), Asst("4"), Usr("5"), Asst("6") }.Select(o => Dump(o)),
            kept.Skip(2).Select(Dump));
    }

    [Fact]
    public void SelectForCompaction_toSummarize_is_exactly_the_middle_slice()
    {
        var working = new List<object> { Sys("prompt") };
        for (var i = 0; i < 8; i++) working.Add(Usr(i.ToString()));
        var (toSummarize, kept) = CodeContext.SelectForCompaction(working, keepLastTurns: 4);
        Assert.Equal(new[] { "0", "1", "2", "3" }, toSummarize.Select(m => (string)m.GetType().GetProperty("content")!.GetValue(m)!));
        Assert.Equal(5, kept.Count);   // 1 system + 4 kept
    }

    [Fact]
    public void SelectForCompaction_with_no_system_messages_still_works()
    {
        var working = new List<object>();
        for (var i = 0; i < 6; i++) working.Add(Usr(i.ToString()));
        var (toSummarize, kept) = CodeContext.SelectForCompaction(working, keepLastTurns: 4);
        Assert.Equal(2, toSummarize.Count);
        Assert.Equal(4, kept.Count);
        Assert.False(CodeContext.IsSystemMessage(kept[0]));
    }

    [Fact]
    public void SelectForCompaction_with_exactly_keepLastTurns_compacts_nothing()
    {
        var working = new List<object> { Sys("prompt"), Usr("1"), Usr("2"), Usr("3"), Usr("4") };
        var (toSummarize, kept) = CodeContext.SelectForCompaction(working, keepLastTurns: 4);
        Assert.Empty(toSummarize);
        Assert.Equal(5, kept.Count);
    }

    private static string Dump(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    // ---- RenderTranscript ----

    [Fact]
    public void RenderTranscript_renders_role_colon_content_lines()
    {
        var text = CodeContext.RenderTranscript(new object[] { Usr("do the thing"), Asst("done") });
        Assert.Contains("user: do the thing", text);
        Assert.Contains("assistant: done", text);
    }

    [Fact]
    public void RenderTranscript_clips_long_content()
    {
        var longText = new string('x', 2000);
        var text = CodeContext.RenderTranscript(new object[] { ToolMsg("Bash", longText) }, clipChars: 500);
        Assert.Contains("tool(Bash): " + new string('x', 500), text);
        Assert.DoesNotContain(new string('x', 501), text);
    }

    [Fact]
    public void RenderTranscript_names_the_tool_for_a_tool_result_message()
    {
        var text = CodeContext.RenderTranscript(new object[] { ToolMsg("Read", "file contents") });
        Assert.Contains("tool(Read): file contents", text);
    }

    [Fact]
    public void RenderTranscript_summarizes_a_null_content_toolcall_assistant_turn()
    {
        var toolCallMsg = new
        {
            role = "assistant",
            content = (string?)null,
            tool_calls = new object[] { new { id = "call_0", type = "function", function = new { name = "Edit", arguments = "{}" } } },
        };
        var text = CodeContext.RenderTranscript(new object[] { toolCallMsg });
        Assert.Contains("assistant: [called Edit]", text);
    }

    // ---- CompactAsync (network-free short-circuit only — see class doc) ----

    [Fact]
    public async System.Threading.Tasks.Task CompactAsync_with_nothing_to_compact_leaves_working_unchanged_and_never_calls_the_model()
    {
        var working = new List<object> { Sys("prompt"), Usr("hi"), Asst("hello") };   // only 2 non-system turns
        var before = new List<object>(working);
        var (ok, message) = await CodeContext.CompactAsync("C:\\nowhere", working, null, "", default);
        Assert.True(ok);
        Assert.Contains("nothing to compact", message);
        Assert.Equal(before.Select(Dump), working.Select(Dump));
    }
}
