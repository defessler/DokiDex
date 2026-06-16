using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure core of the steerable rewriter: stripping an LLM's chatty wrapping down to the bare prompt. The
// live call needs agent mode, but CleanRewrite is total + side-effect-free, so it's locked here with no GPU.
public class RewriterTests
{
    [Fact]
    public void Plain_prompt_passes_through_trimmed()
        => Assert.Equal("a fox in the rain", Rewriter.CleanRewrite("  a fox in the rain  "));

    [Fact]
    public void Leading_preamble_line_is_dropped()
    {
        Assert.Equal("a fox at night", Rewriter.CleanRewrite("Here is the rewritten prompt: a fox at night"));
        Assert.Equal("a fox at night", Rewriter.CleanRewrite("Sure! Here's your revised prompt: a fox at night"));
    }

    [Fact]
    public void Code_fences_are_stripped()
    {
        Assert.Equal("a neon city", Rewriter.CleanRewrite("```\na neon city\n```"));
        Assert.Equal("a neon city", Rewriter.CleanRewrite("```text\na neon city\n```"));
    }

    [Fact]
    public void Surrounding_quotes_are_removed()
    {
        Assert.Equal("a quiet forest", Rewriter.CleanRewrite("\"a quiet forest\""));
        Assert.Equal("a quiet forest", Rewriter.CleanRewrite("“a quiet forest”"));   // smart quotes
    }

    [Fact]
    public void An_inner_apostrophe_is_not_stripped()
        => Assert.Equal("a dragon's lair", Rewriter.CleanRewrite("a dragon's lair"));

    [Fact]
    public void Combined_fence_preamble_and_quotes_are_all_removed()
        => Assert.Equal("a stormy sea",
            Rewriter.CleanRewrite("Sure, here is the revised prompt:\n```\n\"a stormy sea\"\n```"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_input_yields_empty(string? text)
        => Assert.Equal("", Rewriter.CleanRewrite(text));

    [Fact]
    public void Excess_blank_lines_collapse()
        => Assert.Equal("line one\n\nline two", Rewriter.CleanRewrite("line one\n\n\n\nline two"));
}
