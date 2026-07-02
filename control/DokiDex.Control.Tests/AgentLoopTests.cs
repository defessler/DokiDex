using System.Collections.Generic;
using System.Threading;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The bounded agent loop's termination logic, isolated so the hop-cap + the "no tool_calls => stop" rule are
// locked without a GPU/network. The live LLM call (ChatToolsAsync) is integration — it degrades exactly like
// SendAsync when :8080 is down — but the DECISION of whether to take another hop is pure + total + tested here.
// This is the open-model mitigation from decisions.md made verifiable: a SMALL tool set, a BOUNDED loop.
public class AgentLoopTests
{
    private static IReadOnlyList<LocalLlm.ToolCall> Calls(params string[] names)
    {
        var list = new List<LocalLlm.ToolCall>();
        foreach (var n in names) list.Add(new LocalLlm.ToolCall("id_" + n, n, "{}"));
        return list;
    }

    [Fact]
    public void Continue_when_there_are_tool_calls_and_hops_remain()
    {
        // The model asked for a tool and we are under the cap => take another hop.
        Assert.True(Chat.ShouldContinue(Calls("search_library"), hop: 0, maxHops: 4));
        Assert.True(Chat.ShouldContinue(Calls("search_library"), hop: 3, maxHops: 4));
    }

    [Fact]
    public void Stop_when_there_are_no_tool_calls()
    {
        // Graceful fallthrough: plain content with no tool_calls IS the answer — never loop.
        Assert.False(Chat.ShouldContinue(Calls(), hop: 0, maxHops: 4));
        Assert.False(Chat.ShouldContinue(System.Array.Empty<LocalLlm.ToolCall>(), hop: 1, maxHops: 4));
    }

    [Fact]
    public void Stop_once_the_hop_cap_is_reached_even_if_the_model_keeps_calling_tools()
    {
        // A model that loops forever requesting tools must be bounded: at/over the cap, stop regardless.
        Assert.False(Chat.ShouldContinue(Calls("search_library"), hop: 4, maxHops: 4));
        Assert.False(Chat.ShouldContinue(Calls("search_library"), hop: 5, maxHops: 4));
    }

    [Fact]
    public void MaxToolHops_is_the_bounded_default()
        => Assert.Equal(4, Chat.MaxToolHops);

    [Fact]
    public async System.Threading.Tasks.Task AgentAsync_rejects_a_blank_message_without_any_network_call()
    {
        // The same empty-message guard SendAsync uses: a whitespace-only message returns before any :8080 call.
        var r = await Chat.AgentAsync(new ChatRequest(null, null, "  ", null, Tools: true), null, new GalleryService(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("empty message", r.Message);
    }
}
