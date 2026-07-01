using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The fragile heart of the tool-calling agent loop, made pure: extracting choices[0].message.tool_calls[] from a
// llama-swap /v1/chat/completions response. The exact shape is proven by serving/test-toolcall.ps1
// (choices[0].message.tool_calls[].function.{name,arguments}). Total + side-effect-free (no GPU, no network), so
// the open-model risk surfaces (a plain content reply with NO tool_calls => empty; malformed/partial => empty)
// are locked here — mirroring ParseSseDelta / Director.ParseShotlist tested-core discipline.
public class ParseToolCallsTests
{
    [Fact]
    public void A_real_tool_calls_response_yields_the_calls_in_order()
    {
        // The exact shape coder-fast emits (per serving/test-toolcall.ps1): the assistant message carries a
        // tool_calls array, each entry an id + function.{name, arguments(a JSON STRING)}.
        var json = """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
                {"id":"call_abc","type":"function","function":{"name":"search_library","arguments":"{\"query\":\"dragon\"}"}},
                {"id":"call_def","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Tokyo\"}"}}
            ]}}]}
            """;

        var calls = LocalLlm.ParseToolCalls(json);

        Assert.Equal(2, calls.Count);
        Assert.Equal("call_abc", calls[0].Id);
        Assert.Equal("search_library", calls[0].Name);
        Assert.Equal("{\"query\":\"dragon\"}", calls[0].ArgumentsJson);
        Assert.Equal("get_weather", calls[1].Name);
        Assert.Equal("{\"city\":\"Tokyo\"}", calls[1].ArgumentsJson);
    }

    [Fact]
    public void A_plain_content_response_with_no_tool_calls_yields_empty()
    {
        // The graceful fallthrough: when the model just answers (no tool_calls), the loop must see ZERO calls so
        // that the content IS the answer (degrade to normal chat).
        var json = """{"choices":[{"message":{"role":"assistant","content":"Here is the answer."}}]}""";
        Assert.Empty(LocalLlm.ParseToolCalls(json));
    }

    [Fact]
    public void An_empty_tool_calls_array_yields_empty()
    {
        var json = """{"choices":[{"message":{"role":"assistant","content":"hi","tool_calls":[]}}]}""";
        Assert.Empty(LocalLlm.ParseToolCalls(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{}}]}")]
    public void Malformed_or_partial_replies_yield_empty(string json)
        => Assert.Empty(LocalLlm.ParseToolCalls(json));

    [Fact]
    public void A_tool_call_missing_a_name_is_skipped_but_others_survive()
    {
        // A nameless call cannot be dispatched, so it is dropped; a sibling well-formed call still parses.
        var json = """
            {"choices":[{"message":{"tool_calls":[
                {"id":"call_1","function":{"arguments":"{}"}},
                {"id":"call_2","function":{"name":"search_library","arguments":"{\"query\":\"x\"}"}}
            ]}}]}
            """;
        var calls = LocalLlm.ParseToolCalls(json);
        Assert.Single(calls);
        Assert.Equal("search_library", calls[0].Name);
    }

    [Fact]
    public void Missing_arguments_default_to_an_empty_object()
    {
        var json = """{"choices":[{"message":{"tool_calls":[{"function":{"name":"search_library"}}]}}]}""";
        var calls = LocalLlm.ParseToolCalls(json);
        Assert.Single(calls);
        Assert.Equal("search_library", calls[0].Name);
        Assert.Equal("{}", calls[0].ArgumentsJson);   // absent arguments => an empty JSON object, never null
    }

    [Fact]
    public void A_missing_id_gets_a_synthesized_nonempty_id()
    {
        // The model omitted the tool_call id. Defaulting it to "" would let two id-less calls collide on
        // tool_call_id "" — so a unique id is SYNTHESIZED instead (correlation must hold on the role:"tool" turn).
        var json = """{"choices":[{"message":{"tool_calls":[{"function":{"name":"search_library","arguments":"{}"}}]}}]}""";
        var calls = LocalLlm.ParseToolCalls(json);
        Assert.Single(calls);
        Assert.False(string.IsNullOrWhiteSpace(calls[0].Id));   // never blank — a synthesized id stands in
    }

    [Fact]
    public void Two_id_less_calls_get_distinct_synthesized_ids()
    {
        // Two calls in one hop, neither carrying an id: each must get a DISTINCT id so the two role:"tool"
        // results don't collide on the same tool_call_id.
        var json = """
            {"choices":[{"message":{"tool_calls":[
                {"function":{"name":"search_library","arguments":"{\"query\":\"a\"}"}},
                {"function":{"name":"search_library","arguments":"{\"query\":\"b\"}"}}
            ]}}]}
            """;
        var calls = LocalLlm.ParseToolCalls(json);
        Assert.Equal(2, calls.Count);
        Assert.False(string.IsNullOrWhiteSpace(calls[0].Id));
        Assert.False(string.IsNullOrWhiteSpace(calls[1].Id));
        Assert.NotEqual(calls[0].Id, calls[1].Id);   // distinct, so correlation never collides
    }

    [Fact]
    public void An_object_typed_arguments_value_is_preserved_as_a_json_string()
    {
        // Some open/local models emit `arguments` as a JSON OBJECT instead of the OpenAI JSON-STRING. The old
        // behavior DROPPED it to "{}" — silently losing the model's intent (search_library would list recent
        // items instead of searching "dragon"). We now SERIALIZE the object back to a JSON string so the
        // downstream arg-parsers (ParseQuery / MapGenArgs / …) recover the real arguments. Tool-call reliability fix.
        var json = """{"choices":[{"message":{"tool_calls":[{"id":"call_x","function":{"name":"search_library","arguments":{"query":"dragon"}}}]}}]}""";
        var calls = LocalLlm.ParseToolCalls(json);
        Assert.Single(calls);
        Assert.Equal("search_library", calls[0].Name);
        Assert.Equal("{\"query\":\"dragon\"}", calls[0].ArgumentsJson);   // object preserved verbatim, not dropped to "{}"
    }

    [Fact]
    public void A_non_object_non_string_arguments_value_still_falls_back_to_an_empty_object()
    {
        // Only OBJECT-typed arguments carry a usable payload. A JSON array/number/bool isn't a valid arguments
        // object for any tool, so it still degrades to "{}" (the downstream parsers expect an object) — never throws.
        var json = """{"choices":[{"message":{"tool_calls":[{"id":"call_y","function":{"name":"search_library","arguments":[1,2,3]}}]}}]}""";
        var calls = LocalLlm.ParseToolCalls(json);
        Assert.Single(calls);
        Assert.Equal("{}", calls[0].ArgumentsJson);
    }
}
