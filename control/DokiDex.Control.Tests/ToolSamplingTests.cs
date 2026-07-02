using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The tool-call sampling tightening (research §5.4): the agent loop's request body must carry a LOW temperature
// plus min_p/top_p so open models select tools reliably, while every conversational/vision/director caller stays
// byte-for-byte (no min_p/top_p) on the temp-0.8 path — which also keeps the --cache-reuse prefix intact.
// LocalLlm.Body is the pure seam (internal via InternalsVisibleTo); these lock the invariant.
public class ToolSamplingTests
{
    private static readonly object[] Msgs = { new { role = "user", content = "hi" } };

    [Fact]
    public void Tool_call_body_carries_low_temp_min_p_and_top_p()
    {
        var b = LocalLlm.Body(Msgs, 0.1, 1024, "coder-fast", minP: 0.1, topP: 0.9);
        Assert.Equal(0.1, (double)b["temperature"]!);
        Assert.Equal(0.1, (double)b["min_p"]!);
        Assert.Equal(0.9, (double)b["top_p"]!);
    }

    [Fact]
    public void Conversational_body_omits_min_p_and_top_p()
    {
        // The chat/vision/director/rewriter callers pass no minP/topP => the keys are ABSENT, so those requests are
        // byte-for-byte unchanged (and the --cache-reuse prefix is preserved). Only the tool path tightens sampling.
        var b = LocalLlm.Body(Msgs, 0.8, 1024, "coder-fast");
        Assert.False(b.ContainsKey("min_p"));
        Assert.False(b.ContainsKey("top_p"));
        Assert.Equal(0.8, (double)b["temperature"]!);
    }
}
