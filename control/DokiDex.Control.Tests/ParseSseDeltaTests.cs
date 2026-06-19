using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The fragile bit of token streaming, made pure: extracting choices[0].delta.content from ONE upstream OpenAI
// SSE 'data: {...}' line emitted by llama-swap. Total + side-effect-free (no GPU, no network), so the framing
// edge cases ([DONE] sentinel, keepalives, role-only first chunk, quotes/newlines in a token) are locked here —
// mirroring DirectorTests/VisionTests/ChatPromptTests tested-core discipline.
public class ParseSseDeltaTests
{
    [Fact]
    public void Normal_content_delta_yields_the_text()
    {
        var line = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}";
        Assert.Equal("Hello", LocalLlm.ParseSseDelta(line));
    }

    [Fact]
    public void Done_sentinel_yields_null()
    {
        Assert.Null(LocalLlm.ParseSseDelta("data: [DONE]"));
    }

    [Fact]
    public void Empty_or_whitespace_or_non_data_keepalive_yields_null()
    {
        Assert.Null(LocalLlm.ParseSseDelta(""));
        Assert.Null(LocalLlm.ParseSseDelta("   "));
        Assert.Null(LocalLlm.ParseSseDelta(": keepalive"));
        Assert.Null(LocalLlm.ParseSseDelta("event: ping"));
    }

    [Fact]
    public void Role_only_first_chunk_with_no_content_yields_null()
    {
        // The first streamed chunk is typically {"delta":{"role":"assistant"}} with no content yet.
        var line = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}";
        Assert.Null(LocalLlm.ParseSseDelta(line));
    }

    [Fact]
    public void A_delta_with_quotes_and_newlines_is_returned_verbatim()
    {
        // Token content containing a quote and an escaped newline must come back exactly, byte for byte.
        var line = "data: {\"choices\":[{\"delta\":{\"content\":\"say \\\"hi\\\"\\nthen go\"}}]}";
        Assert.Equal("say \"hi\"\nthen go", LocalLlm.ParseSseDelta(line));
    }
}
