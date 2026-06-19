using System.Collections.Generic;
using System.Text.Json;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The per-hop transcript SHAPING in the agent loop, made pure: AppendToolRound appends ONE assistant tool-call
// turn followed by ONE role:"tool" message per executed call onto the working transcript. Previously this body
// lived inline in AgentAsync (only ShouldContinue + the empty-guard were tested); extracting it lets the OpenAI
// wire-shape be locked with NO GPU/network: the assistant turn carries content:null + the tool_calls, each result
// is role:"tool" with the matching tool_call_id, and ordering is assistant-THEN-results. Round-tripping through
// JSON proves the anonymous objects serialize to exactly the shape llama-swap expects.
public class AgentTranscriptTests
{
    // Serialize one appended message (anonymous object) and parse it back so we can assert on the wire shape.
    private static JsonElement Wire(object message)
        => JsonDocument.Parse(JsonSerializer.Serialize(message)).RootElement.Clone();

    [Fact]
    public void Appends_the_assistant_tool_call_turn_then_one_tool_result_per_call_in_order()
    {
        var working = new List<object> { new { role = "user", content = "find dragons" } };
        var calls = new List<LocalLlm.ToolCall>
        {
            new("call_0", "search_library", "{\"query\":\"dragon\"}"),
            new("call_1", "search_library", "{\"query\":\"sunset\"}"),
        };
        var results = new[] { "2 items: dragon-a, dragon-b", "1 item: sunset-x" };

        Chat.AppendToolRound(working, assistantContent: "let me search", calls, results);

        // The seed user turn + 1 assistant turn + 2 tool results = 4 entries, in that order.
        Assert.Equal(4, working.Count);

        var assistant = Wire(working[1]);
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var tcs = assistant.GetProperty("tool_calls");
        Assert.Equal(2, tcs.GetArrayLength());
        Assert.Equal("call_0", tcs[0].GetProperty("id").GetString());
        Assert.Equal("search_library", tcs[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"query\":\"dragon\"}", tcs[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call_1", tcs[1].GetProperty("id").GetString());

        // Each result is a role:"tool" message carrying the MATCHING tool_call_id, in call order.
        var t0 = Wire(working[2]);
        Assert.Equal("tool", t0.GetProperty("role").GetString());
        Assert.Equal("call_0", t0.GetProperty("tool_call_id").GetString());
        Assert.Equal("2 items: dragon-a, dragon-b", t0.GetProperty("content").GetString());

        var t1 = Wire(working[3]);
        Assert.Equal("tool", t1.GetProperty("role").GetString());
        Assert.Equal("call_1", t1.GetProperty("tool_call_id").GetString());
        Assert.Equal("1 item: sunset-x", t1.GetProperty("content").GetString());
    }

    [Fact]
    public void The_assistant_turn_content_is_null_when_it_carries_tool_calls()
    {
        // OpenAI convention: an assistant message that carries tool_calls has content:null (not ""). Passing an
        // empty assistant content must still serialize content as JSON null.
        var working = new List<object>();
        var calls = new List<LocalLlm.ToolCall> { new("call_0", "search_library", "{}") };

        Chat.AppendToolRound(working, assistantContent: "", calls, new[] { "ok" });

        var assistant = Wire(working[0]);
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
    }
}
