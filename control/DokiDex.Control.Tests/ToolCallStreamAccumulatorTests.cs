using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The fragile heart of STREAMING tool-calling (1.1), made pure: ToolCallStreamAccumulator is fed the JSON payload
// of each upstream SSE 'data:' line (the network loop in LocalLlm.ChatToolsStreamAsync strips the "data:" prefix
// and skips '[DONE]'/blank before calling Push) and, at stream end, turns the accumulated fragments into the SAME
// ToolCall shape ParseToolCalls produces from a non-streaming reply. No network/GPU — total + side-effect-free,
// mirroring ParseSseDelta/ParseToolCalls tested-core discipline. Written FIRST (red) before ChatToolsStreamAsync
// was wired up, per the plan's TDD instruction.
public class ToolCallStreamAccumulatorTests
{
    [Fact]
    public void Content_only_chunks_are_returned_live_and_accumulated_for_Finish()
    {
        var acc = new ToolCallStreamAccumulator();

        Assert.Equal("Hel", acc.Push("""{"choices":[{"delta":{"content":"Hel"}}]}"""));
        Assert.Equal("lo!", acc.Push("""{"choices":[{"delta":{"content":"lo!"}}]}"""));

        var (content, calls, finishReason) = acc.Finish();
        Assert.Equal("Hello!", content);
        Assert.Empty(calls);
        Assert.Null(finishReason);
        Assert.False(acc.HasMalformedArguments);
    }

    [Fact]
    public void Finish_reason_is_recorded_from_a_content_less_chunk()
    {
        var acc = new ToolCallStreamAccumulator();
        Assert.Equal("hi", acc.Push("""{"choices":[{"delta":{"content":"hi"}}]}"""));
        Assert.Null(acc.Push("""{"choices":[{"delta":{},"finish_reason":"stop"}]}"""));
        var (content, calls, finishReason) = acc.Finish();
        Assert.Equal("hi", content);
        Assert.Empty(calls);
        Assert.Equal("stop", finishReason);
    }

    [Fact]
    public void A_single_tool_call_fragmented_across_four_chunks_reassembles()
    {
        var acc = new ToolCallStreamAccumulator();

        // Real llama.cpp/OpenAI streaming shape: id/name arrive on the first fragment for an index; arguments
        // trickle in as raw string pieces that only form valid JSON once fully concatenated.
        Assert.Null(acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_abc","function":{"name":"Edit","arguments":""}}]}}]}"""));
        Assert.Null(acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}"""));
        Assert.Null(acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\","}}]}}]}"""));
        Assert.Null(acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"search\":\"x\"}"}}]}}]}"""));
        Assert.Null(acc.Push("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""));

        var (content, calls, finishReason) = acc.Finish();
        Assert.Equal("", content);
        Assert.Equal("tool_calls", finishReason);
        Assert.Single(calls);
        Assert.Equal("call_abc", calls[0].Id);
        Assert.Equal("Edit", calls[0].Name);
        Assert.Equal("""{"path":"a.txt","search":"x"}""", calls[0].ArgumentsJson);
        Assert.False(acc.HasMalformedArguments);
    }

    [Fact]
    public void Two_interleaved_tool_calls_by_index_do_not_cross_contaminate()
    {
        var acc = new ToolCallStreamAccumulator();

        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"Read"}}]}}]}""");
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_2","function":{"name":"Grep"}}]}}]}""");
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"a\""}}]}}]}""");
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"pattern\":\"b\""}}]}}]}""");
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"}"}}]}}]}""");
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"}"}}]}}]}""");

        var (_, calls, _) = acc.Finish();
        Assert.Equal(2, calls.Count);
        Assert.Equal("Read", calls[0].Name);
        Assert.Equal("""{"path":"a"}""", calls[0].ArgumentsJson);
        Assert.Equal("Grep", calls[1].Name);
        Assert.Equal("""{"pattern":"b"}""", calls[1].ArgumentsJson);
    }

    [Fact]
    public void A_missing_id_is_synthesized_from_the_index()
    {
        var acc = new ToolCallStreamAccumulator();
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"Bash","arguments":"{}"}}]}}]}""");
        var (_, calls, _) = acc.Finish();
        Assert.Single(calls);
        Assert.Equal("call_0", calls[0].Id);
    }

    [Fact]
    public void Blank_or_absent_arguments_default_to_an_empty_object()
    {
        var acc = new ToolCallStreamAccumulator();
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_x","function":{"name":"Bash"}}]}}]}""");
        var (_, calls, _) = acc.Finish();
        Assert.Single(calls);
        Assert.Equal("{}", calls[0].ArgumentsJson);
    }

    [Fact]
    public void A_nameless_call_is_skipped()
    {
        var acc = new ToolCallStreamAccumulator();
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_x","function":{"arguments":"{}"}}]}}]}""");
        var (_, calls, _) = acc.Finish();
        Assert.Empty(calls);
    }

    [Fact]
    public void Reasoning_content_is_dropped_entirely_never_shown_never_accumulated()
    {
        var acc = new ToolCallStreamAccumulator();
        var shown = acc.Push("""{"choices":[{"delta":{"reasoning_content":"the model's private chain of thought"}}]}""");
        Assert.Null(shown);   // never surfaced live via onToken
        var (content, _, _) = acc.Finish();
        Assert.Equal("", content);   // never lands in the transcript either
    }

    [Fact]
    public void Malformed_fragment_json_is_detected_only_at_Finish()
    {
        var acc = new ToolCallStreamAccumulator();
        // Fragments are routinely partial (invalid on their own) mid-stream — Push must not misfire here.
        acc.Push("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_x","function":{"name":"Edit","arguments":"{\"path\":"}}]}}]}""");
        Assert.False(acc.HasMalformedArguments);   // not checked until Finish()

        // The model's stream ends WITHOUT ever closing the JSON — the concatenated arguments are broken.
        var (_, calls, _) = acc.Finish();
        Assert.True(acc.HasMalformedArguments);
        Assert.Single(calls);   // the call still surfaces (with its broken args) so the caller can decide to retry
    }

    [Fact]
    public void An_empty_or_whitespace_or_non_data_payload_yields_null_and_never_throws()
    {
        var acc = new ToolCallStreamAccumulator();
        Assert.Null(acc.Push(""));
        Assert.Null(acc.Push("   "));
        Assert.Null(acc.Push("not json at all"));
        Assert.Null(acc.Push("{}"));
        Assert.Null(acc.Push("""{"choices":[]}"""));
        var (content, calls, finishReason) = acc.Finish();
        Assert.Equal("", content);
        Assert.Empty(calls);
        Assert.Null(finishReason);
    }

    // ---- usage frame capture (1.6) ----
    // llama.cpp (given `stream_options: {include_usage: true}` on the request — LocalLlm.ChatToolsStreamAsync sets
    // this) emits a FINAL SSE chunk carrying `usage` at the ROOT, typically alongside an EMPTY or absent `choices`
    // array — so Usage must be read BEFORE the choices-array early-return, or this chunk's usage would never be seen.

    [Fact]
    public void Usage_frame_with_empty_choices_is_captured_without_affecting_content()
    {
        var acc = new ToolCallStreamAccumulator();
        Assert.Equal("hi", acc.Push("""{"choices":[{"delta":{"content":"hi"}}]}"""));
        Assert.Null(acc.Usage);   // not yet seen

        var shown = acc.Push("""{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":34}}""");
        Assert.Null(shown);   // no displayable content in a usage-only frame
        Assert.NotNull(acc.Usage);
        Assert.Equal(120, acc.Usage!.PromptTokens);
        Assert.Equal(34, acc.Usage.CompletionTokens);

        var (content, _, _) = acc.Finish();
        Assert.Equal("hi", content);   // the usage frame never pollutes accumulated content
    }

    [Fact]
    public void Usage_frame_with_absent_choices_is_still_captured()
    {
        var acc = new ToolCallStreamAccumulator();
        Assert.Null(acc.Push("""{"usage":{"prompt_tokens":5,"completion_tokens":2}}"""));
        Assert.NotNull(acc.Usage);
        Assert.Equal(5, acc.Usage!.PromptTokens);
        Assert.Equal(2, acc.Usage.CompletionTokens);
    }

    [Fact]
    public void No_usage_frame_leaves_Usage_null_the_degrade_path()
    {
        var acc = new ToolCallStreamAccumulator();
        acc.Push("""{"choices":[{"delta":{"content":"hi"}}]}""");
        acc.Push("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""");
        Assert.Null(acc.Usage);   // an llama.cpp build that ignores stream_options never emits the frame
    }
}
