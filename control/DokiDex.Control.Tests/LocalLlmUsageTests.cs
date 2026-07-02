using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Usage capture (1.6), blocking path: llama.cpp's /v1/chat/completions response carries a top-level `usage`
// object (`{prompt_tokens, completion_tokens}`) alongside `choices`. LocalLlm.ParseUsage is the pure extraction —
// total, side-effect-free, mirroring ParseToolCalls/ParseSseDelta's tested-core discipline — used by ChatToolsAsync
// to set LocalLlm.LastUsage after every blocking call.
public class LocalLlmUsageTests
{
    [Fact]
    public void Reads_prompt_and_completion_tokens_when_present()
    {
        var json = """{"choices":[{"message":{"content":"hi"}}],"usage":{"prompt_tokens":42,"completion_tokens":7}}""";
        var usage = LocalLlm.ParseUsage(json);
        Assert.NotNull(usage);
        Assert.Equal(42, usage!.PromptTokens);
        Assert.Equal(7, usage.CompletionTokens);
    }

    [Fact]
    public void Missing_a_field_defaults_it_to_zero_rather_than_failing_the_whole_parse()
    {
        var json = """{"usage":{"prompt_tokens":10}}""";
        var usage = LocalLlm.ParseUsage(json);
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"choices":[{"message":{"content":"hi"}}]}""")]   // no `usage` at all — an older llama.cpp build
    public void Absent_or_malformed_usage_degrades_to_null_never_throws(string? json)
        => Assert.Null(LocalLlm.ParseUsage(json));
}
